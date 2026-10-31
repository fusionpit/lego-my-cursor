// Touchpad report correction, independent of WDF request handling.
// Firmware bug: a finger lift arrives as tip down with X and/or Y = 0xFFFF (above PAD_MAX), then tip up with
// the same bytes. Windows clamps the value to PAD_MAX, so the cursor jumps to the pad's edge or corner.
// Report ID 1, 20 bytes: [0] id=1 | contact i at o=1+5*i (i=0..2): [o] status (bit0 confidence, bit1 tip,
// bits2-4 id), [o+1..o+2] X u16 LE, [o+3..o+4] Y u16 LE | [16..17] scan time | [18] contact count | [19] button.
#pragma once
#include <windows.h>

static const int REPORT_ID = 1;
static const int REPORT_LEN = 20;
static const int PAD_MAX = 1024; // logical max from the HID descriptor

// Params::mode values.
static const ULONG MODE_PASS_THROUGH = 0;
static const ULONG MODE_STATELESS = 1;
static const ULONG MODE_STATEFUL = 2;

// Contact i starts at FIRST_CONTACT + CONTACT_LEN * i.
static const int FIRST_CONTACT = 1;
static const int CONTACT_LEN = 5;
static const int CONTACT_SLOTS = 3;

// Byte offsets within a contact.
static const int STATUS = 0;
static const int X_LO = 1;
static const int X_HI = 2;
static const int Y_LO = 3;
static const int Y_HI = 4;

// Status byte: tip bit, contact ID bits.
static const int TIP = 2;
static const int ID_SHIFT = 2;
static const int ID_MASK = 7;

// Registry setting: Mode, a REG_DWORD in the driver Parameters key, read once per device and clamped to 2.
// Fixed: the HID descriptor's 1024 bound and the PTP requirement to lift at the last position.
struct Params
{
    ULONG mode = MODE_STATELESS; // 0 pass-through, 1 stateless tip-clear, 2 stateful
};

// Last in-range X/Y and tip state the host was sent.
struct TrackedContact
{
    USHORT x;
    USHORT y;
    bool known;
    bool tipDown;
};
struct PadState
{
    SRWLOCK lock;
    TrackedContact contacts[8];
}; // indexed by contact ID; zero-init is valid

// Mode 1: clear tip for firmware lifts reported as tip down with X/Y = 0xFFFF.
static void FixReport(BYTE *report)
{
    for (int i = 0; i < CONTACT_SLOTS; i++)
    {
        BYTE *contact = report + FIRST_CONTACT + CONTACT_LEN * i;
        int x = contact[X_LO] | (contact[X_HI] << 8);
        int y = contact[Y_LO] | (contact[Y_HI] << 8);
        if ((contact[STATUS] & TIP) && (x > PAD_MAX || y > PAD_MAX))
            contact[STATUS] &= ~TIP; // tip only: confidence=0 means palm (PTP spec)
    }
}

// Mode 2: replace out-of-range coordinates with the contact's last in-range X/Y, and lift at that position.
// Every in-range tip-down sample is accepted unchanged. Caller holds state.lock.
static void FixReportStateful(BYTE *report, PadState &state)
{
    for (int i = 0; i < CONTACT_SLOTS; i++)
    {
        BYTE *contact = report + FIRST_CONTACT + CONTACT_LEN * i;
        if (!(contact[STATUS] | contact[X_LO] | contact[X_HI] | contact[Y_LO] | contact[Y_HI]))
            continue; // unused slot; would read as contact 0 lifting
        TrackedContact &tracked = state.contacts[(contact[STATUS] >> ID_SHIFT) & ID_MASK];
        const bool tip = (contact[STATUS] & TIP) != 0;
        const int raw[2] = {contact[X_LO] | (contact[X_HI] << 8), contact[Y_LO] | (contact[Y_HI] << 8)};
        int value[2] = {raw[0], raw[1]};
        if (raw[0] > PAD_MAX || raw[1] > PAD_MAX)
        { // Firmware lift frame: keep the last known position. A glitch frame cannot start a touch.
            if (!tracked.known || (!tracked.tipDown && tip))
                contact[STATUS] &= ~TIP;
            if (tracked.known)
            {
                value[0] = tracked.x;
                value[1] = tracked.y;
            }
        }
        else if (tip)
        {
            tracked.x = (USHORT)raw[0];
            tracked.y = (USHORT)raw[1];
            tracked.known = true;
        }
        else if (tracked.known)
        { // Lift at the last position the host saw.
            value[0] = tracked.x;
            value[1] = tracked.y;
        }
        contact[X_LO] = (BYTE)value[0];
        contact[X_HI] = (BYTE)(value[0] >> 8);
        contact[Y_LO] = (BYTE)value[1];
        contact[Y_HI] = (BYTE)(value[1] >> 8);
        tracked.tipDown = (contact[STATUS] & TIP) != 0;
    }
}

// Shared read-path boundary: the completion's reported length must never exceed owned capacity.
// Reader state is protected per report.
static void ProcessReports(BYTE *buffer, size_t length, size_t capacity, PadState *state, ULONG mode)
{
    if (mode == MODE_PASS_THROUGH || !buffer)
        return;
    if (length > capacity)
        length = capacity;
    for (size_t offset = 0; length >= REPORT_LEN && offset <= length - REPORT_LEN; offset += REPORT_LEN)
    {
        if (buffer[offset] != REPORT_ID)
            continue;
        if (mode != MODE_STATEFUL || !state)
        {
            FixReport(buffer + offset); // also mode 2's fallback when the request has no file object
            continue;
        }
        AcquireSRWLockExclusive(&state->lock);
        FixReportStateful(buffer + offset, *state);
        ReleaseSRWLockExclusive(&state->lock);
    }
}
