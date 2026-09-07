
use std::collections::HashMap;
use std::ffi::{c_char, CString};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::thread::{self, JoinHandle};

use ashpd::desktop::{
    remote_desktop::{DeviceType, KeyState, RemoteDesktop},
    screencast::{CursorMode, Screencast, SourceType},
    PersistMode,
};

const SCROLL_UNIT: f64 = 15.0;

enum Msg {

    Motion { u: f64, v: f64 },
    TouchDown { slot: u32, u: f64, v: f64 },
    TouchMotion { slot: u32, u: f64, v: f64 },
    TouchUp { slot: u32 },
    Scroll { steps: i32 },
    Key { keysym: i32, pressed: bool },
    Button { button: i32, pressed: bool },
    Stop,
}

/// Outcome of the worker's `Session::close` call, so `db_linux_input_stop` can report
/// whether the portal actually released the session rather than silently succeeding.
const CLOSE_PENDING: i32 = 1;
const CLOSE_OK: i32 = 0;
const CLOSE_FAILED: i32 = -2;

struct InputSession {
    tx: async_channel::Sender<Msg>,
    thread: Option<JoinHandle<()>>,
    close_result: std::sync::Arc<std::sync::atomic::AtomicI32>,
}

impl InputSession {
    fn stop(&mut self) -> i32 {
        let _ = self.tx.try_send(Msg::Stop);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
        self.close_result.load(Ordering::SeqCst)
    }
}

static SESSIONS: OnceLock<Mutex<HashMap<u64, InputSession>>> = OnceLock::new();
static NEXT_SESSION_ID: AtomicU64 = AtomicU64::new(1);

fn sessions() -> &'static Mutex<HashMap<u64, InputSession>> {
    SESSIONS.get_or_init(|| Mutex::new(HashMap::new()))
}

static LAST_ERROR: OnceLock<Mutex<Option<CString>>> = OnceLock::new();

pub(crate) fn set_last_error(msg: &str) {
    let cell = LAST_ERROR.get_or_init(|| Mutex::new(None));
    if let Ok(mut guard) = cell.lock() {
        *guard = CString::new(msg).ok();
    }
    log::warn!("[DesktopBuddy input] {msg}");
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_last_error() -> *const c_char {
    let Some(cell) = LAST_ERROR.get() else {
        return std::ptr::null();
    };
    match cell.lock() {
        Ok(guard) => match guard.as_ref() {
            Some(s) => s.as_ptr(),
            None => std::ptr::null(),
        },
        Err(_) => std::ptr::null(),
    }
}

struct Ready {
    width: f64,
    height: f64,
}

struct SessionInfo {
    node_id: u32,
    width: f64,
    height: f64,
    is_monitor: bool,
    token: Option<String>,
}

async fn setup<'a>(
    remote: &RemoteDesktop<'a>,
    screencast: &Screencast<'a>,
    session: &ashpd::desktop::Session<'a, RemoteDesktop<'a>>,
    token: Option<&str>,
) -> Result<(Ready, u32, bool, Option<String>), String> {
    remote
        .select_devices(
            session,
            DeviceType::Keyboard | DeviceType::Pointer | DeviceType::Touchscreen,
            token,
            PersistMode::ExplicitlyRevoked,
        )
        .await
        .map_err(|e| format!("select_devices: {e}"))?;

    screencast
        .select_sources(
            session,
            CursorMode::Embedded,
            SourceType::Monitor | SourceType::Window | SourceType::Virtual,
            false,
            None,
            PersistMode::DoNot,
        )
        .await
        .map_err(|e| format!("select_sources: {e}"))?;

    let response = remote
        .start(session, None)
        .await
        .map_err(|e| format!("start: {e}"))?
        .response()
        .map_err(|e| format!("start response: {e}"))?;

    let restore_token = response.restore_token().map(|s| s.to_owned());
    let streams = response.streams().ok_or_else(|| "no streams".to_string())?;
    let stream = streams.first().ok_or_else(|| "empty streams".to_string())?;
    let node_id = stream.pipe_wire_node_id();
    let (w, h) = stream.size().unwrap_or((0, 0));
    let is_monitor = matches!(stream.source_type(), Some(SourceType::Monitor));
    Ok((
        Ready {
            width: w.max(1) as f64,
            height: h.max(1) as f64,
        },
        node_id,
        is_monitor,
        restore_token,
    ))
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_start(
    token_ptr: *const u8,
    token_len: usize,
    out_session_id: *mut u64,
    out_selection: *mut crate::DbLinuxSelection,
) -> i32 {
    if out_session_id.is_null() {
        return -1;
    }
    unsafe { *out_session_id = 0 };

    let token: Option<String> = if token_ptr.is_null() || token_len == 0 {
        None
    } else {
        let slice = unsafe { std::slice::from_raw_parts(token_ptr, token_len) };
        std::str::from_utf8(slice).ok().map(|s| s.to_owned())
    };

    let (tx, rx) = async_channel::unbounded::<Msg>();
    let (ready_tx, ready_rx) = std::sync::mpsc::channel::<Result<SessionInfo, String>>();

    let close_result = std::sync::Arc::new(std::sync::atomic::AtomicI32::new(CLOSE_PENDING));
    let close_slot = close_result.clone();

    let thread = thread::spawn(move || {
        async_std::task::block_on(async move {
            let remote = match RemoteDesktop::new().await {
                Ok(r) => r,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("RemoteDesktop::new: {e}")));
                    return;
                }
            };
            let screencast = match Screencast::new().await {
                Ok(s) => s,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("Screencast::new: {e}")));
                    return;
                }
            };
            let session = match remote.create_session().await {
                Ok(s) => s,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("create_session: {e}")));
                    return;
                }
            };

            let (ready, node_id, is_monitor, restore_token) = match setup(&remote, &screencast, &session, token.as_deref()).await
            {
                Ok(v) => v,
                Err(e) => {
                    // The session exists from create_session above even though setup failed
                    // (a cancelled picker lands here), so it needs closing like any other.
                    let _ = session.close().await;
                    let _ = ready_tx.send(Err(e));
                    return;
                }
            };

            let stream_node = node_id;
            let _ = ready_tx.send(Ok(SessionInfo {
                node_id,
                width: ready.width,
                height: ready.height,
                is_monitor,
                token: restore_token,
            }));

            while let Ok(msg) = rx.recv().await {
                match msg {
                    Msg::Motion { u, v } => {
                        let x = (u.clamp(0.0, 1.0)) * ready.width;
                        let y = (v.clamp(0.0, 1.0)) * ready.height;
                        if let Err(e) = remote
                            .notify_pointer_motion_absolute(&session, stream_node, x, y)
                            .await
                        {
                            set_last_error(&format!("notify_pointer_motion_absolute: {e}"));
                        }
                    }
                    Msg::TouchDown { slot, u, v } => {
                        let x = u.clamp(0.0, 1.0) * ready.width;
                        let y = v.clamp(0.0, 1.0) * ready.height;
                        if let Err(e) = remote
                            .notify_touch_down(&session, stream_node, slot, x, y)
                            .await
                        {
                            set_last_error(&format!("notify_touch_down: {e}"));
                        }
                    }
                    Msg::TouchMotion { slot, u, v } => {
                        let x = u.clamp(0.0, 1.0) * ready.width;
                        let y = v.clamp(0.0, 1.0) * ready.height;
                        if let Err(e) = remote
                            .notify_touch_motion(&session, stream_node, slot, x, y)
                            .await
                        {
                            set_last_error(&format!("notify_touch_motion: {e}"));
                        }
                    }
                    Msg::TouchUp { slot } => {
                        if let Err(e) = remote.notify_touch_up(&session, slot).await {
                            set_last_error(&format!("notify_touch_up: {e}"));
                        }
                    }
                    Msg::Scroll { steps } => {
                        let dy = steps as f64 * SCROLL_UNIT;
                        if let Err(e) = remote.notify_pointer_axis(&session, 0.0, dy, true).await {
                            set_last_error(&format!("notify_pointer_axis: {e}"));
                        }
                    }
                    Msg::Key { keysym, pressed } => {
                        let state = if pressed {
                            KeyState::Pressed
                        } else {
                            KeyState::Released
                        };
                        if let Err(e) = remote.notify_keyboard_keysym(&session, keysym, state).await {
                            set_last_error(&format!("notify_keyboard_keysym: {e}"));
                        }
                    }
                    Msg::Button { button, pressed } => {
                        let state = if pressed {
                            KeyState::Pressed
                        } else {
                            KeyState::Released
                        };
                        if let Err(e) = remote.notify_pointer_button(&session, button, state).await {
                            set_last_error(&format!("notify_pointer_button: {e}"));
                        }
                    }
                    Msg::Stop => break,
                }
            }

            // Dropping the Session does not end it: Close is a D-Bus call and Drop cannot
            // await. ashpd reuses a cached session-bus connection that outlives this thread,
            // so without this the portal keeps the session alive for the life of the process
            // and the desktop environment leaves a screencast indicator behind for each share.
            match session.close().await {
                Ok(()) => close_slot.store(CLOSE_OK, Ordering::SeqCst),
                Err(e) => {
                    set_last_error(&format!("session close: {e}"));
                    close_slot.store(CLOSE_FAILED, Ordering::SeqCst);
                }
            }
        });
    });

    let info = match ready_rx.recv() {
        Ok(Ok(info)) => info,
        Ok(Err(e)) => {
            set_last_error(&format!("session setup failed: {e}"));
            let _ = thread.join();
            return -2;
        }
        Err(_) => {
            set_last_error("session setup thread ended without reporting a result");
            return -3;
        }
    };

    if !out_selection.is_null() {
        let mut sel = crate::DbLinuxSelection {
            node_id: info.node_id,
            width: info.width.max(1.0) as u32,
            height: info.height.max(1.0) as u32,
            is_monitor: if info.is_monitor { 1 } else { 0 },
            ..Default::default()
        };
        if let Some(tok) = &info.token {
            let bytes = tok.as_bytes();
            let count = bytes.len().min(sel.restore_token.len().saturating_sub(1));
            sel.restore_token[..count].copy_from_slice(&bytes[..count]);
            sel.restore_token_len = count as u32;
        }
        unsafe { *out_selection = sel };
    }

    let id = NEXT_SESSION_ID.fetch_add(1, Ordering::Relaxed);
    let mut map = match sessions().lock() {
        Ok(m) => m,
        Err(_) => return -4,
    };
    map.insert(
        id,
        InputSession {
            tx,
            thread: Some(thread),
            close_result,
        },
    );
    unsafe { *out_session_id = id };
    0
}

fn send(session_id: u64, msg: Msg) -> bool {
    if let Ok(map) = sessions().lock() {
        if let Some(s) = map.get(&session_id) {
            return s.tx.try_send(msg).is_ok();
        }
    }
    false
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_motion(session_id: u64, u: f64, v: f64) {
    send(session_id, Msg::Motion { u, v });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_touch_down(session_id: u64, slot: u32, u: f64, v: f64) -> i32 {
    if send(session_id, Msg::TouchDown { slot, u, v }) {
        0
    } else {
        -1
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_touch_motion(session_id: u64, slot: u32, u: f64, v: f64) {
    send(session_id, Msg::TouchMotion { slot, u, v });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_touch_up(session_id: u64, slot: u32) {
    send(session_id, Msg::TouchUp { slot });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_scroll(session_id: u64, steps: i32) {
    send(session_id, Msg::Scroll { steps });
}

/// Presses or releases a pointer button. `button` is an evdev code: BTN_LEFT is 0x110,
/// BTN_RIGHT 0x111, BTN_MIDDLE 0x112.
///
/// The rest of the input surface is touch-based, which has no notion of a secondary button;
/// this is the only path that can produce a real right-click.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_button(session_id: u64, button: i32, pressed: i32) {
    send(session_id, Msg::Button { button, pressed: pressed != 0 });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_key(session_id: u64, keysym: i32, pressed: i32) {
    send(
        session_id,
        Msg::Key {
            keysym,
            pressed: pressed != 0,
        },
    );
}

fn kwin_effect_call(method: &str, effect: &str) -> Result<bool, String> {
    async_std::task::block_on(async {
        let connection = ashpd::zbus::Connection::session()
            .await
            .map_err(|e| format!("session bus: {e}"))?;

        let reply = connection
            .call_method(
                Some("org.kde.KWin"),
                "/Effects",
                Some("org.kde.kwin.Effects"),
                method,
                &(effect,),
            )
            .await
            .map_err(|e| format!("{method}: {e}"))?;

        // loadEffect/unloadEffect return nothing; only isEffectLoaded carries a body.
        Ok(reply.body().deserialize::<bool>().unwrap_or(true))
    })
}

fn effect_name(ptr: *const u8, len: usize) -> Option<String> {
    if ptr.is_null() || len == 0 {
        return None;
    }
    let slice = unsafe { std::slice::from_raw_parts(ptr, len) };
    std::str::from_utf8(slice).ok().map(|s| s.to_owned())
}

/// Looks up the connector name (`DP-5`, `HDMI-A-1`, ...) of the output whose geometry matches
/// the given rectangle, writing it into `out_buf` and returning its length.
///
/// The ScreenCast portal deliberately hands back only a node id and geometry, never an output
/// name, so the name has to be recovered by matching that geometry against what the
/// compositor reports. KWin's `supportInformation` is the only interface that exposes it.
/// Returns 0 when no output matches, negative on error.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_kwin_output_name(
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    out_buf: *mut u8,
    buf_len: usize,
) -> i32 {
    if out_buf.is_null() || buf_len == 0 {
        return -1;
    }

    let info = match kwin_support_information() {
        Ok(s) => s,
        Err(e) => {
            set_last_error(&format!("kwin_output_name: {e}"));
            return -2;
        }
    };

    let wanted = format!("{x},{y},{width}x{height}");
    let Some(name) = find_output_named(&info, &wanted) else {
        return 0;
    };

    let bytes = name.as_bytes();
    let len = bytes.len().min(buf_len);
    unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, len) };
    len as i32
}

fn kwin_support_information() -> Result<String, String> {
    async_std::task::block_on(async {
        let connection = ashpd::zbus::Connection::session()
            .await
            .map_err(|e| format!("session bus: {e}"))?;

        let reply = connection
            .call_method(
                Some("org.kde.KWin"),
                "/KWin",
                Some("org.kde.KWin"),
                "supportInformation",
                &(),
            )
            .await
            .map_err(|e| format!("supportInformation: {e}"))?;

        reply
            .body()
            .deserialize::<String>()
            .map_err(|e| format!("supportInformation body: {e}"))
    })
}

/// Pairs each `Name:` with the `Geometry:` that follows it and returns the matching name.
///
/// Entries that are not outputs (the backend's own `Name: DRM`, for instance) are followed by
/// another `Name:` rather than a geometry, so they never match and drop out naturally.
fn find_output_named(info: &str, wanted_geometry: &str) -> Option<String> {
    let mut current: Option<String> = None;

    for line in info.lines() {
        let line = line.trim();
        if let Some(rest) = line.strip_prefix("Name: ") {
            current = Some(rest.trim().to_owned());
        } else if let Some(rest) = line.strip_prefix("Geometry: ") {
            if rest.trim() == wanted_geometry {
                return current.clone();
            }
        }
    }

    None
}

/// Reports whether a KWin effect is currently loaded: 1 yes, 0 no, negative on error.
///
/// Used to suspend KWin's `shakecursor` effect while a desktop is shared. Injected pointer
/// motion and the user's real mouse fight over the cursor, which KWin reads as shaking and
/// responds to by magnifying the cursor.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_kwin_effect_loaded(name_ptr: *const u8, name_len: usize) -> i32 {
    let Some(name) = effect_name(name_ptr, name_len) else {
        return -1;
    };

    match kwin_effect_call("isEffectLoaded", &name) {
        Ok(true) => 1,
        Ok(false) => 0,
        Err(e) => {
            set_last_error(&format!("kwin_effect_loaded: {e}"));
            -2
        }
    }
}

/// Loads (`load` non-zero) or unloads a KWin effect. Returns 0 on success.
///
/// This is deliberately a runtime-only change rather than a kwinrc edit: if Resonite dies
/// while an effect is suspended, the user's configuration is untouched and the effect comes
/// back on the next KWin reconfigure or restart.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_kwin_effect_set(name_ptr: *const u8, name_len: usize, load: i32) -> i32 {
    let Some(name) = effect_name(name_ptr, name_len) else {
        return -1;
    };

    let method = if load != 0 { "loadEffect" } else { "unloadEffect" };
    match kwin_effect_call(method, &name) {
        Ok(_) => 0,
        Err(e) => {
            set_last_error(&format!("kwin_effect_set: {e}"));
            -2
        }
    }
}

/// Revokes a persisted RemoteDesktop grant.
///
/// `select_devices` above uses `PersistMode::ExplicitlyRevoked`, which is what lets a saved
/// source be re-shared without a dialog. The cost is that every grant outlives the process
/// and stays in the desktop's remembered-permissions list until something deletes it, so a
/// token we are about to replace or forget has to be revoked here or it leaks forever.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_revoke_token(token_ptr: *const u8, token_len: usize) -> i32 {
    const TABLE: &str = "remote-desktop";
    db_linux_portal_revoke_token(TABLE.as_ptr(), TABLE.len(), token_ptr, token_len)
}

/// Revokes a persisted grant from a named permission-store table.
///
/// RemoteDesktop grants land in `remote-desktop` and ScreenCast grants in `screencast`, so
/// the table has to be chosen by the caller rather than assumed.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_portal_revoke_token(
    table_ptr: *const u8,
    table_len: usize,
    token_ptr: *const u8,
    token_len: usize,
) -> i32 {
    if token_ptr.is_null() || token_len == 0 || table_ptr.is_null() || table_len == 0 {
        return -1;
    }

    let token_slice = unsafe { std::slice::from_raw_parts(token_ptr, token_len) };
    let table_slice = unsafe { std::slice::from_raw_parts(table_ptr, table_len) };
    let (Ok(token), Ok(table)) = (
        std::str::from_utf8(token_slice),
        std::str::from_utf8(table_slice),
    ) else {
        return -2;
    };
    let token = token.to_owned();
    let table = table.to_owned();

    async_std::task::block_on(async move {
        let connection = match ashpd::zbus::Connection::session().await {
            Ok(c) => c,
            Err(e) => {
                set_last_error(&format!("revoke_token: session bus: {e}"));
                return -3;
            }
        };

        match connection
            .call_method(
                Some("org.freedesktop.impl.portal.PermissionStore"),
                "/org/freedesktop/impl/portal/PermissionStore",
                Some("org.freedesktop.impl.portal.PermissionStore"),
                "Delete",
                &(table.as_str(), token.as_str()),
            )
            .await
        {
            Ok(_) => 0,
            Err(e) => {
                set_last_error(&format!("revoke_token {table}/{token}: {e}"));
                -4
            }
        }
    })
}

/// Stops a session and reports what actually happened, so a portal session that was never
/// released does not look identical to a clean shutdown from the caller's side.
///
/// Returns 0 when the portal confirmed the close, 1 if the worker ended without reaching it,
/// -1 if no such session was registered, -2 if the close call itself failed, -3 on lock
/// poisoning.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_stop(session_id: u64) -> i32 {
    match sessions().lock() {
        Ok(mut map) => match map.remove(&session_id) {
            Some(mut s) => s.stop(),
            None => {
                // An empty map means the library was unloaded and reloaded between start and
                // stop, taking these statics (and the worker threads) with it. A populated map
                // means something else already removed this id. The two need different fixes.
                let ids: Vec<String> = map.keys().map(|k| k.to_string()).collect();
                set_last_error(&format!(
                    "input_stop: session {session_id} not registered; map holds {} session(s): [{}]; next_id={}",
                    ids.len(),
                    ids.join(","),
                    NEXT_SESSION_ID.load(Ordering::Relaxed)
                ));
                -1
            }
        },
        Err(_) => -3,
    }
}
