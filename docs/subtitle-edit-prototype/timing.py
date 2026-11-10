"""Times SE's ffmpeg command and shotdetect (both decoders) on each clip, interleaved, N rounds: wall and CPU (children)."""
import os, resource, subprocess, sys, time
LIBS = os.environ["SHOTDETECTOR_FFMPEG_LIBS"]
CLI = "src/ShotDetector.Cli/bin/Release/net10.0/shotdetect"
rounds, out, clips = int(sys.argv[1]), sys.argv[2], sys.argv[3:]
def cmd(m, v):
    if m == "ffmpeg":
        return ["ffmpeg", "-hide_banner", "-nostats", "-i", v, "-vf", "select=gt(scene\\,0.4),showinfo", "-threads", "0", "-f", "null", "-"]
    return [CLI, "-i", v, "--ffmpeg-dir", LIBS, "--decoder", m, "-d", "adaptive", "-t", "3", "--save-shotchanges", out]
for v in clips:
    for r in range(rounds):
        for m in ["ffmpeg", "inprocess", "process"]:
            a = resource.getrusage(resource.RUSAGE_CHILDREN); t = time.perf_counter()
            subprocess.run(cmd(m, v), capture_output=True, check=True)
            w = time.perf_counter() - t; b = resource.getrusage(resource.RUSAGE_CHILDREN)
            print(os.path.basename(v), m, f"{w:.2f}", f"{b.ru_utime + b.ru_stime - a.ru_utime - a.ru_stime:.2f}", flush=True)
