# Kainos DominoEX / THOR

A Kainos module for **DominoEX** and **THOR** keyboard chat, timed and coded as fldigi does, so it works with fldigi
and the other programs that have them.

Both are IFK+ (incremental frequency keying): 18 tones, each symbol a step of 2 to 17 tones up from the last. The data
is in the step, not the tone, so they don't mind a little mistuning or drift, and a tone never repeats.

| Family | Modes | What it is |
|---|---|---|
| DominoEX | Micro, 4, 5, 8, 11, 16, 22, 44, 88 | 4 bits a symbol, its own varicode, no error correction: quick to read |
| THOR | Micro, 4, 5, 8, 11, 16, 22, 25 x4, 50 x1, 50 x2, 100 | DominoEX's signal with error correction (K = 7, or K = 15 for 25 x4 and faster) and interleaving, so it rides through static crashes and fades |

The number is about the baud. Modes 5, 11 and 22 are defined at 11025 samples a second, as in fldigi; the module
handles that itself. The widest (THOR 25 x4 to 100, DominoEX 88) are 1.5 to 1.8 kHz wide: put them near the middle of
the passband.

Install it in Kainos from **Setup > Modules**, then open it from the **Digi Modes** menu (**DominoEX / THOR**).

## Receiving

- **Click a signal on the waterfall** to read it. The click snaps to the signal's centre. Being off by a tone or two
  doesn't matter.
- **SQL** (squelch) shows text only from a signal strong enough: off, low, medium, high, very high. The bar shows the
  signal-to-noise as fldigi works it out.
- **Rev** turns the tones the other way up, for a station on the other sideband. LSB / DIGL is allowed for already.
- THOR's text arrives a little behind the signal: the decoder waits to be sure, and the interleaver spreads each bit
  out in time.

## Sending

- Type in the box and press **TX** (Ctrl+T): the box goes out, then whatever you type, as you type it. **RX**
  (Ctrl+R) goes back to receive once everything typed has gone; **Esc** stops at once.
- **F1 – F8** send the messages under them (right-click one to change it). They fill in `<MYCALL>`, `<MYNAME>`,
  `<MYGRID>`, `<CALL>`, `<NAME>`, `<RST>` and `<MODE>`; `<RX>` goes back to receive when it's reached.
- Each transmission starts and ends as fldigi's do (CR STX CR, then CR EOT CR). THOR sends more NULs at the end than
  fldigi does, so the last characters come out at the other end without waiting for noise to push them through.
- **Tune** sends a steady tone for up to 30 s. Set the transmit level (Options) and drive for full power with the
  ALC barely moving.

## Logging

**Log QSO** writes an ADIF log (MODE DOMINO with SUBMODE DOMINOEX, or MODE THOR with SUBMODE THOR16 ...) in the
module's data folder and tells logging programs as WSJT-X does (UDP "Logged ADIF" to 127.0.0.1:2237 by default), for
Log4OM, N1MM+, HRD, DXKeeper, GridTracker and the Kainos Logbook.

## Building

`.\build.ps1` builds it, runs the self-test (the varicodes and THOR's decoder; every mode sent and received clean,
with noise and mistuned; the other sideband; 44.1 kHz; the transmit path) and packages
`dist\kainos-domino-<version>.zip`.

GPLv3 or later. Copyright (C) 2026 Justin Cron K7JUS.
