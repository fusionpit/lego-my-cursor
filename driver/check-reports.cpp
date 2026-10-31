// Small executable check of the same report-processing boundary used by OnReadDone.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "reportfix.h"

static void Check(bool condition, const char *message)
{
    if (!condition)
    {
        fprintf(stderr, "Report check failed: %s\n", message);
        exit(1);
    }
}

static void Contact(BYTE *report, int slot, BYTE status, USHORT x, USHORT y)
{
    BYTE *contact = report + FIRST_CONTACT + CONTACT_LEN * slot;
    contact[STATUS] = status;
    contact[X_LO] = (BYTE)x;
    contact[X_HI] = (BYTE)(x >> 8);
    contact[Y_LO] = (BYTE)y;
    contact[Y_HI] = (BYTE)(y >> 8);
}

static bool At(const BYTE *report, int slot, USHORT x, USHORT y)
{
    const BYTE *contact = report + FIRST_CONTACT + CONTACT_LEN * slot;
    return contact[X_LO] == (BYTE)x && contact[X_HI] == (BYTE)(x >> 8) && contact[Y_LO] == (BYTE)y &&
           contact[Y_HI] == (BYTE)(y >> 8);
}

static bool Tip(const BYTE *report, int slot)
{
    return (report[FIRST_CONTACT + CONTACT_LEN * slot + STATUS] & TIP) != 0;
}

// Processes a 20-byte report with the given completion length and capacity; reports whether any byte changed.
static bool Changed(BYTE *report, PadState *state, ULONG mode, size_t length = REPORT_LEN, size_t capacity = REPORT_LEN)
{
    BYTE original[REPORT_LEN];
    memcpy(original, report, sizeof(original));
    ProcessReports(report, length, capacity, state, mode);
    return memcmp(report, original, sizeof(original)) != 0;
}

int main()
{
    Check(Params{}.mode == MODE_STATELESS, "stateless is the default mode");
    PadState state = {};
    BYTE report[REPORT_LEN] = {REPORT_ID};

    // Mode 0 leaves every byte alone.
    Contact(report, 0, TIP | 1, 0xFFFF, 0xFFFF);
    Check(!Changed(report, &state, MODE_PASS_THROUGH), "pass-through with state");

    // Mode 1 clears tip on out-of-range contacts only.
    Check(Changed(report, NULL, MODE_STATELESS) && !Tip(report, 0) && At(report, 0, 0xFFFF, 0xFFFF),
          "stateless clears tip and keeps coordinates");
    Contact(report, 0, TIP | 1, 500, 500);
    Check(!Changed(report, NULL, MODE_STATELESS), "stateless leaves in-range contact alone");
    Contact(report, 0, TIP | 1, 0xFFFF, 0xFFFF);
    report[0] = 2;
    Check(!Changed(report, NULL, MODE_STATELESS), "other report ID ignored");
    report[0] = REPORT_ID;

    Check(!Changed(report, NULL, MODE_STATELESS, REPORT_LEN, REPORT_LEN - 1),
          "owned capacity wins over reported length");
    Check(!Changed(report, NULL, MODE_STATELESS, REPORT_LEN - 1, REPORT_LEN), "short completion ignored");

    BYTE packed[REPORT_LEN * 2 + 4] = {};
    packed[0] = packed[REPORT_LEN] = REPORT_ID;
    Contact(packed, 0, TIP | 1, 0xFFFF, 0xFFFF);
    Contact(packed, 1, TIP | 1 | (1 << ID_SHIFT), 300, 300);
    Contact(packed + REPORT_LEN, 0, TIP | 1, 12, 0xFFFF);
    // Odd-length tail: a partial report with tip set and X = 0xFFFF that must stay untouched.
    const BYTE tail[4] = {REPORT_ID, TIP | 1, 0xFF, 0xFF};
    memcpy(packed + REPORT_LEN * 2, tail, sizeof(tail));
    ProcessReports(packed, sizeof(packed), sizeof(packed), NULL, MODE_STATELESS);
    Check(!Tip(packed, 0) && Tip(packed, 1) && At(packed, 1, 300, 300), "packed first report");
    Check(!Tip(packed + REPORT_LEN, 0) && At(packed + REPORT_LEN, 0, 12, 0xFFFF), "packed second report");
    Check(!memcmp(packed + REPORT_LEN * 2, tail, sizeof(tail)), "partial trailing report untouched");

    // Mode 2: every in-range tip-down sample passes through unchanged, however far it jumps.
    Contact(report, 0, TIP | 1, 500, 500);
    Check(!Changed(report, &state, MODE_STATEFUL), "valid contact");
    Contact(report, 0, TIP | 1, 10, 10);
    Check(!Changed(report, &state, MODE_STATEFUL), "large jump passes through unchanged");

    // Observed on the device: lift with X valid and only Y = 0xFFFF, tip still set, then tip clear with the same
    // bytes. Both restore X and Y to the last accepted position; the first keeps tip.
    Contact(report, 0, TIP | 1, 12, 0xFFFF);
    Check(Changed(report, &state, MODE_STATEFUL) && Tip(report, 0) && At(report, 0, 10, 10),
          "one-axis lift restores both coordinates and keeps tip");
    Contact(report, 0, 1, 12, 0xFFFF);
    Check(Changed(report, &state, MODE_STATEFUL) && !Tip(report, 0) && At(report, 0, 10, 10),
          "tip-up with bad bytes restored");
    Contact(report, 0, 1, 501, 502);
    Check(Changed(report, &state, MODE_STATEFUL) && At(report, 0, 10, 10), "lift moved to last position");

    // A known, lifted contact that reappears tip-down at 0xFFFF gets its tip cleared and its position restored.
    Contact(report, 0, TIP | 1, 0xFFFF, 0xFFFF);
    Check(Changed(report, &state, MODE_STATEFUL) && !Tip(report, 0) && At(report, 0, 10, 10),
          "glitch after lift cannot start a touch");

    // A glitch frame on a contact with no accepted position clears tip and keeps the raw bytes.
    Contact(report, 0, TIP | 1 | (3 << ID_SHIFT), 0xFFFF, 0xFFFF);
    Check(Changed(report, &state, MODE_STATEFUL) && !Tip(report, 0) && At(report, 0, 0xFFFF, 0xFFFF),
          "unknown contact glitch clears tip");

    // Without a file object, mode 2 falls back to stateless tip clearing.
    Contact(report, 0, TIP | 1, 0xFFFF, 0xFFFF);
    Check(Changed(report, NULL, MODE_STATEFUL) && !Tip(report, 0) && At(report, 0, 0xFFFF, 0xFFFF),
          "null state falls back to stateless");
    puts("Report-processing checks passed.");
    return 0;
}
