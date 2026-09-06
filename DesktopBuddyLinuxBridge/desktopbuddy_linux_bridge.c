#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdint.h>
#include <string.h>

#define IMAGE_FILE_MACHINE_AMD64 0x8664
#define IMAGE_FILE_EXECUTABLE_IMAGE 0x0002
#define IMAGE_FILE_LARGE_ADDRESS_AWARE 0x0020
#define IMAGE_FILE_DLL 0x2000
#define IMAGE_NT_OPTIONAL_HDR64_MAGIC 0x20b
#define IMAGE_SUBSYSTEM_WINDOWS_GUI 2
#define IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE 0x0040
#define IMAGE_DLLCHARACTERISTICS_NX_COMPAT 0x0100

typedef struct ImageFileHeader {
  uint16_t Machine;
  uint16_t NumberOfSections;
  uint32_t TimeDateStamp;
  uint32_t PointerToSymbolTable;
  uint32_t NumberOfSymbols;
  uint16_t SizeOfOptionalHeader;
  uint16_t Characteristics;
} ImageFileHeader;

typedef struct ImageDataDirectory {
  uint32_t VirtualAddress;
  uint32_t Size;
} ImageDataDirectory;

typedef struct ImageOptionalHeader64 {
  uint16_t Magic;
  uint8_t MajorLinkerVersion;
  uint8_t MinorLinkerVersion;
  uint32_t SizeOfCode;
  uint32_t SizeOfInitializedData;
  uint32_t SizeOfUninitializedData;
  uint32_t AddressOfEntryPoint;
  uint32_t BaseOfCode;
  uint64_t ImageBase;
  uint32_t SectionAlignment;
  uint32_t FileAlignment;
  uint16_t MajorOperatingSystemVersion;
  uint16_t MinorOperatingSystemVersion;
  uint16_t MajorImageVersion;
  uint16_t MinorImageVersion;
  uint16_t MajorSubsystemVersion;
  uint16_t MinorSubsystemVersion;
  uint32_t Win32VersionValue;
  uint32_t SizeOfImage;
  uint32_t SizeOfHeaders;
  uint32_t CheckSum;
  uint16_t Subsystem;
  uint16_t DllCharacteristics;
  uint64_t SizeOfStackReserve;
  uint64_t SizeOfStackCommit;
  uint64_t SizeOfHeapReserve;
  uint64_t SizeOfHeapCommit;
  uint32_t LoaderFlags;
  uint32_t NumberOfRvaAndSizes;
  ImageDataDirectory DataDirectory[16];
} ImageOptionalHeader64;

typedef struct ImageNtHeaders64 {
  uint32_t Signature;
  ImageFileHeader FileHeader;
  ImageOptionalHeader64 OptionalHeader;
} ImageNtHeaders64;

typedef struct ImageExportDirectory {
  uint32_t Characteristics;
  uint32_t TimeDateStamp;
  uint16_t MajorVersion;
  uint16_t MinorVersion;
  uint32_t Name;
  uint32_t Base;
  uint32_t NumberOfFunctions;
  uint32_t NumberOfNames;
  uint32_t AddressOfFunctions;
  uint32_t AddressOfNames;
  uint32_t AddressOfNameOrdinals;
} ImageExportDirectory;

typedef struct BuiltinDescriptor {
  ImageNtHeaders64 nt;
  ImageExportDirectory exports;
  uint64_t functions[1];
  uint32_t names[1];
  uint16_t ordinals[1];
  char function_name_0[40];
  char dll_name[32];
} BuiltinDescriptor;

typedef struct DbLinuxFrame {
  int32_t status;
  int32_t fd;
  uint32_t width;
  uint32_t height;
  uint32_t fourcc;
  uint32_t offset;
  int32_t stride;
} DbLinuxFrame;

typedef struct DbLinuxBridgeCall {
  uint32_t op;
  int32_t status;
  uint64_t arg0;
  DbLinuxFrame frame;
  uint64_t buffer;
} DbLinuxBridgeCall;

enum {
  OP_POLL = 2,
  OP_STOP = 3,
  OP_START_NODE = 6,
  OP_COPY_FRAME = 7,
  OP_CLOSE_FRAME = 8,
  OP_LOAD = 9,
};

typedef int32_t (*db_linux_capture_start_node_fn)(uint32_t node_id, uint64_t *capture_id);
typedef int32_t (*db_linux_capture_poll_fn)(uint64_t capture_id, DbLinuxFrame *frame);
typedef void (*db_linux_capture_stop_fn)(uint64_t capture_id);
typedef int32_t (*db_linux_frame_copy_and_close_fn)(DbLinuxFrame *frame, uint8_t *dest, size_t dest_size);
typedef void (*db_linux_frame_close_fn)(DbLinuxFrame *frame);

static void *g_native;
static db_linux_capture_start_node_fn g_start_node;
static db_linux_capture_poll_fn g_poll;
static db_linux_capture_stop_fn g_stop;
static db_linux_frame_copy_and_close_fn g_copy_close;
static db_linux_frame_close_fn g_close;

static int32_t load_native(void) {
  if (g_native)
    return 0;

  Dl_info info;
  if (!dladdr((void *)&load_native, &info) || !info.dli_fname)
    return -100;

  char path[4096];
  const char *slash = strrchr(info.dli_fname, '/');
  if (!slash)
    return -101;

  size_t dir_len = (size_t)(slash - info.dli_fname);
  if (dir_len + sizeof("/libdesktopbuddy_linux_native.so") >= sizeof(path))
    return -102;

  memcpy(path, info.dli_fname, dir_len);
  strcpy(path + dir_len, "/libdesktopbuddy_linux_native.so");

  g_native = dlopen(path, RTLD_NOW | RTLD_LOCAL);
  if (!g_native)
    return -103;

  g_start_node = (db_linux_capture_start_node_fn)dlsym(g_native, "db_linux_capture_start_node");
  g_poll = (db_linux_capture_poll_fn)dlsym(g_native, "db_linux_capture_poll");
  g_stop = (db_linux_capture_stop_fn)dlsym(g_native, "db_linux_capture_stop");
  g_copy_close = (db_linux_frame_copy_and_close_fn)dlsym(g_native, "db_linux_frame_copy_and_close");
  g_close = (db_linux_frame_close_fn)dlsym(g_native, "db_linux_frame_close");

  if (!g_start_node || !g_poll || !g_stop) {
    dlclose(g_native);
    g_native = NULL;
    return -104;
  }

  return 0;
}

__attribute__((visibility("default"), ms_abi))
int32_t DesktopBuddyLinuxBridgeCall(DbLinuxBridgeCall *call) {
  if (!call)
    return -1;

  int32_t status = load_native();
  if (status != 0) {
    call->status = status;
    return status;
  }

  /* Loading the Rust library drags PipeWire and its dependencies into the Wine process, which
     is as capable of faulting as the capture connect that follows it. Every op loads lazily,
     so a crash on the first call could be either one. Exposing the load on its own lets the
     caller record a survived dlopen before it attempts a connect, which narrows a dead
     renderer down to a single stage. */
  if (call->op == OP_LOAD) {
    call->status = 0;
    return 0;
  }

  if (call->op == OP_POLL) {
    memset(&call->frame, 0, sizeof(call->frame));
    call->frame.fd = -1;
    call->status = g_poll(call->arg0, &call->frame);
    return call->status;
  }

  if (call->op == OP_STOP) {
    g_stop(call->arg0);
    call->status = 0;
    return 0;
  }

  if (call->op == OP_START_NODE) {
    uint64_t capture_id = 0;
    call->status = g_start_node((uint32_t)call->arg0, &capture_id);
    if (call->status == 0)
      call->arg0 = capture_id;
    return call->status;
  }

  if (call->op == OP_COPY_FRAME) {
    call->status = g_copy_close ? g_copy_close(&call->frame, (uint8_t *)(uintptr_t)call->buffer, call->arg0) : -1;
    return call->status;
  }

  if (call->op == OP_CLOSE_FRAME) {
    if (g_close) g_close(&call->frame);
    call->status = 0;
    return 0;
  }

  call->status = -2;
  return -2;
}

__attribute__((visibility("default")))
BuiltinDescriptor __wine_spec_nt_header = {
  .nt = {
    .Signature = 0x00004550,
    .FileHeader = {
      .Machine = IMAGE_FILE_MACHINE_AMD64,
      .NumberOfSections = 2,
      .SizeOfOptionalHeader = sizeof(ImageOptionalHeader64),
      .Characteristics = IMAGE_FILE_EXECUTABLE_IMAGE | IMAGE_FILE_LARGE_ADDRESS_AWARE | IMAGE_FILE_DLL,
    },
    .OptionalHeader = {
      .Magic = IMAGE_NT_OPTIONAL_HDR64_MAGIC,
      .SectionAlignment = 0x1000,
      .FileAlignment = 0x1000,
      .MajorOperatingSystemVersion = 6,
      .MajorSubsystemVersion = 6,
      .SizeOfImage = 0x20000,
      .SizeOfHeaders = 0x1000,
      .Subsystem = IMAGE_SUBSYSTEM_WINDOWS_GUI,
      .DllCharacteristics = IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE | IMAGE_DLLCHARACTERISTICS_NX_COMPAT,
      .SizeOfStackReserve = 0x100000,
      .SizeOfStackCommit = 0x1000,
      .SizeOfHeapReserve = 0x100000,
      .SizeOfHeapCommit = 0x1000,
      .NumberOfRvaAndSizes = 16,
      .DataDirectory = {
        [0] = {
          .VirtualAddress = (uint32_t)((uintptr_t)&((BuiltinDescriptor *)0)->exports),
          .Size = sizeof(ImageExportDirectory),
        },
      },
    },
  },
  .exports = {
    .Name = (uint32_t)((uintptr_t)&((BuiltinDescriptor *)0)->dll_name),
    .Base = 1,
    .NumberOfFunctions = 1,
    .NumberOfNames = 1,
    .AddressOfFunctions = (uint32_t)((uintptr_t)&((BuiltinDescriptor *)0)->functions),
    .AddressOfNames = (uint32_t)((uintptr_t)&((BuiltinDescriptor *)0)->names),
    .AddressOfNameOrdinals = (uint32_t)((uintptr_t)&((BuiltinDescriptor *)0)->ordinals),
  },
  .names = {
    (uint32_t)((uintptr_t)&((BuiltinDescriptor *)0)->function_name_0),
  },
  .ordinals = {0},
  .function_name_0 = "DesktopBuddyLinuxBridgeCall",
  .dll_name = "DesktopBuddyLinuxBridge",
};

__attribute__((constructor))
static void init_builtin_descriptor(void) {
  uintptr_t header = (uintptr_t)&__wine_spec_nt_header;
  uintptr_t entry = (uintptr_t)&DesktopBuddyLinuxBridgeCall;
  uintptr_t base = header < entry ? header : entry;
  uintptr_t top = header > entry ? header : entry;
  uintptr_t image_base = base & ~(uintptr_t)0xffff;
  __wine_spec_nt_header.nt.OptionalHeader.ImageBase = image_base;
  __wine_spec_nt_header.nt.OptionalHeader.SizeOfImage = (uint32_t)(((top - image_base) + 0x1ffffu) & ~0xffffu);
  __wine_spec_nt_header.functions[0] = (uint64_t)(uintptr_t)&DesktopBuddyLinuxBridgeCall;
}
