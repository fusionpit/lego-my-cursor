// LeGo My Cursor: UMDF 2 upper filter on the Legion Go touchpad collection.
// Patches the firmware's out-of-range (0xFFFF) coordinates on finger lift.
// Report layout: see reportfix.h.
#define WIN32_NO_STATUS // let wdf.h pull in ntstatus.h without redefinition warnings
#include <windows.h>
#undef WIN32_NO_STATUS
#include <wdf.h>
#include "reportfix.h"

// WDF zero-fills the context and runs no constructor; EvtDeviceAdd assigns params before the queue exists,
// and the read path only reads them afterwards.
WDF_DECLARE_CONTEXT_TYPE(Params);   // device: Mode setting read once at device add
WDF_DECLARE_CONTEXT_TYPE(PadState); // file object: hidclass gives each reader its own report stream

static EVT_WDF_REQUEST_COMPLETION_ROUTINE OnReadDone;
static EVT_WDF_IO_QUEUE_IO_READ OnRead;
static EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
extern "C" DRIVER_INITIALIZE DriverEntry;

_Use_decl_annotations_ static void OnReadDone(WDFREQUEST request, WDFIOTARGET,
                                              PWDF_REQUEST_COMPLETION_PARAMS completion, WDFCONTEXT context)
{
    const Params &params = *(Params *)context;
    NTSTATUS status = completion->IoStatus.Status;
    size_t length = completion->IoStatus.Information;
    BYTE *buffer;
    size_t capacity;
    // A buffer shorter than one report cannot hold a report to fix; complete it unchanged.
    if (NT_SUCCESS(status) &&
        NT_SUCCESS(WdfRequestRetrieveOutputBuffer(request, REPORT_LEN, (PVOID *)&buffer, &capacity)))
    {
        // The INF allows null file objects; use stateless correction without per-reader state.
        WDFFILEOBJECT fileObject = params.mode == MODE_STATEFUL ? WdfRequestGetFileObject(request) : NULL;
        PadState *state = fileObject ? WdfObjectGet_PadState(fileObject) : NULL;
        // The lock serializes updates, not completion order. A stale tip=1 after lift can leave tipDown set
        // and report the old position on the next lift; the next accepted frame recovers. Ordering checks
        // are deferred based on the 2026-10-28 Go capture:
        // 48,390 reports (~7m10s, one reader), at most one pending read/callback. Scan time (bytes 16-17)
        // was monotonic in 30,694 driver and 741 stock reports except a 16-bit wrap after 37 s idle.
        // Raw Input exposed this reader's completions, not the PTP stack's reader, so this is no guarantee.
        // Revisit on overlap, reordering, or read-path changes: skip updates for strictly older scan times
        // (mod 2^16). Accept equal times: 12 reports advanced coordinates without advancing scan time,
        // indicating non-atomic firmware writes.
        ProcessReports(buffer, length, capacity, state, params.mode);
    }
    WdfRequestCompleteWithInformation(request, status, completion->IoStatus.Information);
}

_Use_decl_annotations_ static void OnRead(WDFQUEUE queue, WDFREQUEST request, size_t)
{
    WDFDEVICE device = WdfIoQueueGetDevice(queue);
    Params *params = WdfObjectGet_Params(device);
    WdfRequestFormatRequestUsingCurrentType(request);
    WdfRequestSetCompletionRoutine(request, OnReadDone, params);
    if (!WdfRequestSend(request, WdfDeviceGetIoTarget(device), WDF_NO_SEND_OPTIONS))
        WdfRequestComplete(request, WdfRequestGetStatus(request));
}

_Use_decl_annotations_ static NTSTATUS EvtDeviceAdd(WDFDRIVER, PWDFDEVICE_INIT deviceInit)
{
    WdfFdoInitSetFilter(deviceInit);
    // No callbacks: a filter still forwards create/cleanup/close. CannotUseFsContexts leaves FsContext to
    // hidclass; CanBeOptional because the INF lets requests through with no file object.
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, NULL, NULL, NULL);
    fileConfig.FileObjectClass =
        // WDF explicitly permits combining this enum with its optional-file flag.
        // NOLINTNEXTLINE(clang-analyzer-optin.core.EnumCastOutOfRange)
        (WDF_FILEOBJECT_CLASS)(WdfFileObjectWdfCannotUseFsContexts | WdfFileObjectCanBeOptional);
    WDF_OBJECT_ATTRIBUTES fileAttributes;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&fileAttributes, PadState); // zero-filled, and a zeroed SRWLOCK is valid
    WdfDeviceInitSetFileObjectConfig(deviceInit, &fileConfig, &fileAttributes);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, Params);
    WDFDEVICE device;
    NTSTATUS status = WdfDeviceCreate(&deviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status))
        return status;
    // Read once at device start; missing values keep defaults. WDF creates the key on first open:
    // HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\legomycursor\Parameters
    // HKLM\SYSTEM\...\Control\WUDF\Services is absent on Windows 11 build 26300. Service-wide settings follow
    // controller modes (PIDs 61EB/61ED have separate devnodes); device hardware keys would not.
    // INF AddReg defaults would race .Wdf parent creation. Changes require restarting every touchpad devnode.
    // DiInstallDriverW deletes Parameters when recreating the service key, so the installer saves Mode,
    // restores it after installation, and restarts devnodes again to replace the defaults loaded at install.
    Params *params = WdfObjectGet_Params(device);
    *params = Params{}; // replace WDF's zero-filled context with defaults
    WDFKEY key;
    if (NT_SUCCESS(
            WdfDriverOpenParametersRegistryKey(WdfDeviceGetDriver(device), KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &key)))
    {
        DECLARE_CONST_UNICODE_STRING(valueName, L"Mode");
        ULONG value;
        if (NT_SUCCESS(WdfRegistryQueryULong(key, &valueName, &value)))
            params->mode = value;
        WdfRegistryClose(key);
    }
    if (params->mode > MODE_STATEFUL)
        params->mode = MODE_STATEFUL;
    if (params->mode == MODE_PASS_THROUGH)
        return STATUS_SUCCESS;
    // The device is a filter (WdfFdoInitSetFilter), so the framework forwards every request that has no queue
    // to the lower driver; only reads are dispatched here.
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoRead = OnRead;
    WDFQUEUE queue;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
    if (!NT_SUCCESS(status))
        return status;
    return WdfDeviceConfigureRequestDispatching(device, queue, WdfRequestTypeRead);
}

_Use_decl_annotations_ extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(driver, registryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}
