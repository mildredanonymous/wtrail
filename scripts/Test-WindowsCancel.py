"""Exercise the packaged CLI with CTRL_C_EVENT in a dedicated Windows console."""
import ctypes
import json
from pathlib import Path
import shutil
import signal
import subprocess
import sys
from tempfile import TemporaryDirectory
from threading import Timer


def check(command):
    with subprocess.Popen(
        command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8"
    ) as process:
        # Bound the test even if the program stops writing or ignores Ctrl+C.
        deadline = Timer(15, process.kill)
        deadline.start()
        try:
            first = json.loads(process.stdout.readline())
            second = json.loads(process.stdout.readline())
            if (first["type"], second["type"]) != ("start", "result"):
                raise RuntimeError("The CLI did not stream a result before cancellation")
            if not ctypes.WinDLL("kernel32", use_last_error=True).GenerateConsoleCtrlEvent(0, 0):
                raise ctypes.WinError(ctypes.get_last_error())
            output = process.stdout.read()
            error = process.stderr.read()
            process.wait()
            rows = [json.loads(line) for line in output.splitlines()]
            if not rows or rows[-1]["type"] != "finish" or rows[-1]["data"]["exitCode"] != 130:
                raise RuntimeError(f"Missing cancellation record: {error}")
            if process.returncode != 130:
                raise RuntimeError(f"Unexpected exit code: {process.returncode}")
        finally:
            deadline.cancel()
            if process.poll() is None:
                process.kill()


def worker(executable):
    # The Python worker receives the same event. Keep it alive to inspect the child.
    signal.signal(signal.SIGINT, lambda *_: None)
    with TemporaryDirectory(prefix="wtrail-cancel-") as temporary:
        package = Path(temporary) / "package"
        shutil.copytree(executable.parent, package)
        binary = package / executable.name
        log = package / "fixtures" / "auftragsimport" / "logs" / "auftragsimport.log"
        log.write_text(
            "2026-01-15T09:15:01Z ERROR Auftragsimport\n" * 200_000, encoding="utf-8"
        )
        for arguments in (
            ["search", "Auftragsimport", str(log)],
            ["incident", "Auftragsimport", "--demo", "complete", "--order", "found"],
        ):
            check([str(binary), *arguments, "--format", "jsonl"])
            print(f"Windows Ctrl+C with streaming JSONL passed: {arguments[0]}", flush=True)


if __name__ == "__main__":
    if sys.argv[1] == "--worker":
        worker(Path(sys.argv[2]))
    else:
        subprocess.run(
            [sys.executable, __file__, "--worker", str(Path(sys.argv[1]).resolve())],
            creationflags=subprocess.CREATE_NEW_CONSOLE,
            stdout=sys.stdout,
            stderr=sys.stderr,
            timeout=45,
            check=True,
        )
