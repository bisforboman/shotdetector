# CLI audio filter and output (2026-10-11)

Not asked (the user asked for more work to be picked): `--audio-filter "<graph>"` prints analysis filters'
metadata per frame with its time, and `--audio-out <file>` writes the filtered (or plain) audio with AudioWriter, so
#81's tone job (find tone, notch it, write MP3) runs from the command line. Flags like `--probe` and `--waveform`.
