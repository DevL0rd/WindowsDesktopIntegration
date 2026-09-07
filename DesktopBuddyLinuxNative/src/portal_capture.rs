//! Pure ScreenCast capture sessions, separate from the RemoteDesktop input session.
//!
//! Binding ScreenCast to a RemoteDesktop session makes xdg-desktop-portal-kde treat screen
//! sharing as a single `screenShareEnabled` flag over the whole workspace, so the picker
//! offers exactly one source. A standalone ScreenCast session gets the real picker instead,
//! with each monitor listed separately.
//!
//! The session has to stay alive for its PipeWire stream to remain valid, so each one owns a
//! thread that parks until stopped and then closes the portal session explicitly.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::thread::{self, JoinHandle};

use ashpd::desktop::{
    screencast::{CursorMode, Screencast, SourceType},
    PersistMode,
};

use crate::portal_input::set_last_error;

struct CaptureSession {
    stop_tx: async_channel::Sender<()>,
    thread: Option<JoinHandle<()>>,
}

impl CaptureSession {
    fn stop(&mut self) -> i32 {
        let _ = self.stop_tx.try_send(());
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
        0
    }
}

static CAPTURE_SESSIONS: OnceLock<Mutex<HashMap<u64, CaptureSession>>> = OnceLock::new();
static NEXT_CAPTURE_SESSION_ID: AtomicU64 = AtomicU64::new(1);

fn capture_sessions() -> &'static Mutex<HashMap<u64, CaptureSession>> {
    CAPTURE_SESSIONS.get_or_init(|| Mutex::new(HashMap::new()))
}

struct CaptureInfo {
    node_id: u32,
    width: u32,
    height: u32,
    position_x: i32,
    position_y: i32,
    is_monitor: bool,
    token: Option<String>,
}

/// Opens the ScreenCast picker (or restores silently when given a token) and keeps the
/// resulting session alive until `db_linux_screencast_stop`.
///
/// Returns 0 on success, negative on failure.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_screencast_start(
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

    let (stop_tx, stop_rx) = async_channel::unbounded::<()>();
    let (ready_tx, ready_rx) = std::sync::mpsc::channel::<Result<CaptureInfo, String>>();

    let thread = thread::spawn(move || {
        async_std::task::block_on(async move {
            let screencast = match Screencast::new().await {
                Ok(s) => s,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("Screencast::new: {e}")));
                    return;
                }
            };

            let session = match screencast.create_session().await {
                Ok(s) => s,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("create_session: {e}")));
                    return;
                }
            };

            let info = match setup(&screencast, &session, token.as_deref()).await {
                Ok(v) => v,
                Err(e) => {
                    let _ = session.close().await;
                    let _ = ready_tx.send(Err(e));
                    return;
                }
            };

            let _ = ready_tx.send(Ok(info));

            // Park until stopped; the session must outlive the stream.
            let _ = stop_rx.recv().await;

            if let Err(e) = session.close().await {
                set_last_error(&format!("screencast session close: {e}"));
            }
        });
    });

    let info = match ready_rx.recv() {
        Ok(Ok(info)) => info,
        Ok(Err(e)) => {
            set_last_error(&format!("screencast setup failed: {e}"));
            let _ = thread.join();
            return -2;
        }
        Err(_) => {
            set_last_error("screencast setup thread ended without reporting a result");
            return -3;
        }
    };

    if !out_selection.is_null() {
        let mut sel = crate::DbLinuxSelection {
            node_id: info.node_id,
            width: info.width.max(1),
            height: info.height.max(1),
            is_monitor: if info.is_monitor { 1 } else { 0 },
            position_x: info.position_x,
            position_y: info.position_y,
            ..Default::default()
        };
        if let Some(token) = info.token.as_deref() {
            let bytes = token.as_bytes();
            let len = bytes.len().min(sel.restore_token.len());
            sel.restore_token[..len].copy_from_slice(&bytes[..len]);
            sel.restore_token_len = len as u32;
        }
        unsafe { *out_selection = sel };
    }

    let id = NEXT_CAPTURE_SESSION_ID.fetch_add(1, Ordering::Relaxed);
    let mut map = match capture_sessions().lock() {
        Ok(m) => m,
        Err(_) => return -4,
    };
    map.insert(
        id,
        CaptureSession {
            stop_tx,
            thread: Some(thread),
        },
    );
    unsafe { *out_session_id = id };
    0
}

/// Stops a capture session and closes its portal session. Returns 0 on success, -1 if no
/// such session was registered, -3 on lock poisoning.
#[unsafe(no_mangle)]
pub extern "C" fn db_linux_screencast_stop(session_id: u64) -> i32 {
    match capture_sessions().lock() {
        Ok(mut map) => match map.remove(&session_id) {
            Some(mut s) => s.stop(),
            None => -1,
        },
        Err(_) => -3,
    }
}

async fn setup<'a>(
    screencast: &Screencast<'a>,
    session: &ashpd::desktop::Session<'a, Screencast<'a>>,
    token: Option<&str>,
) -> Result<CaptureInfo, String> {
    screencast
        .select_sources(
            session,
            CursorMode::Embedded,
            // Windows are deliberately excluded. The portal exposes no window identity or
            // geometry, so a window panel gets no name, no icon, and input that drifts as soon
            // as the window is moved. Offering it in the picker only invites a broken share.
            SourceType::Monitor | SourceType::Virtual,
            false,
            token,
            PersistMode::ExplicitlyRevoked,
        )
        .await
        .map_err(|e| format!("select_sources: {e}"))?;

    let response = screencast
        .start(session, None)
        .await
        .map_err(|e| format!("start: {e}"))?
        .response()
        .map_err(|e| format!("start response: {e}"))?;

    let restore_token = response.restore_token().map(|s| s.to_owned());
    let stream = response
        .streams()
        .first()
        .ok_or_else(|| "no streams".to_string())?;

    let (w, h) = stream.size().unwrap_or((0, 0));
    let (x, y) = stream.position().unwrap_or((0, 0));

    Ok(CaptureInfo {
        node_id: stream.pipe_wire_node_id(),
        width: w.max(0) as u32,
        height: h.max(0) as u32,
        position_x: x,
        position_y: y,
        is_monitor: matches!(stream.source_type(), Some(SourceType::Monitor)),
        token: restore_token,
    })
}
