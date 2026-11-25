"""Explicit, isolated GPTQ/AWQ runtime setup. Does not install into the source environment."""
import argparse
import importlib.metadata
import json
import os
from pathlib import Path
import subprocess
import sys
import venv

os.environ.update(PYTHONUTF8="1", PIP_DISABLE_PIP_VERSION_CHECK="1")
for output in (sys.stdout, sys.stderr):
    if hasattr(output, "reconfigure"):
        output.reconfigure(encoding="utf-8", errors="replace")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True)
    parser.add_argument("--manifest", required=True)
    args = parser.parse_args()
    target, manifest = Path(args.target), Path(args.manifest)
    if not target.is_absolute() or not manifest.is_absolute():
        parser.error("Use absolute runtime and manifest paths.")
    marker = target / "mason-quantized-runtime.json"
    # Runtimes prepared by earlier builds carry "dbcowboy-" names. They are still recognised as this app's runtime
    # and are replaced by the "mason-" names on the next setup. Keep in step with QuantizedRuntime.cs.
    legacy_marker = target / "dbcowboy-quantized-runtime.json"

    def status(message, **extra):
        manifest.parent.mkdir(parents=True, exist_ok=True)
        temporary = manifest.with_suffix(".tmp")
        temporary.write_text(json.dumps({"status": message, **extra}), encoding="utf-8")
        temporary.replace(manifest)

    try:
        # Probe before making changes. Keep CUDA PyTorch and Transformers fixed to the working source versions.
        status("Checking your existing Python environment…")
        import torch
        import transformers
        from packaging.version import Version
        if not ((3, 10) <= sys.version_info[:2] <= (3, 13)) or Version(transformers.__version__) != Version("5.5.0") or Version(torch.__version__.split("+")[0]) < Version("2.8.0"):
            raise ValueError("Automatic setup currently uses Python 3.10–3.13, Transformers 5.5.0 and PyTorch 2.8+. Choose a matching base Python or choose an existing quantized runtime.")
        source_site = Path(importlib.metadata.distribution("torch").locate_file("")).resolve()
        if target.exists() and any(target.iterdir()) and not marker.exists() and not legacy_marker.exists():
            raise ValueError("The runtime folder contains unrelated files. Choose an existing runtime or use an empty app-owned runtime folder.")
        target.mkdir(parents=True, exist_ok=True)
        provenance = {"sourcePython": sys.executable, "sourceSite": str(source_site), "gptqmodel": "7.0.0", "torch": torch.__version__, "transformers": transformers.__version__}
        existing = marker if marker.exists() else legacy_marker
        if existing.exists() and json.loads(existing.read_text(encoding="utf-8")).get("sourceSite") != str(source_site):
            raise ValueError("This isolated runtime uses a different base Python. Choose its existing executable instead of replacing its dependencies.")
        marker.write_text(json.dumps(provenance, indent=2), encoding="utf-8")
        legacy_marker.unlink(missing_ok=True)
        status("Creating an isolated quantized runtime…")
        venv.EnvBuilder(with_pip=True).create(target)
        python = target / "Scripts" / "python.exe"
        site = target / "Lib" / "site-packages"
        # Append the trusted, selected Python's packages after the isolated overlay. Never change the source environment.
        (site / "mason-base.pth").write_text(str(source_site) + "\n", encoding="utf-8")
        (site / "dbcowboy-base.pth").unlink(missing_ok=True)
        constraints = target / "mason-constraints.txt"
        (target / "dbcowboy-constraints.txt").unlink(missing_ok=True)
        constraints.write_text(f"torch=={torch.__version__}\ntransformers=={transformers.__version__}\n", encoding="utf-8")
        status("Installing GPTQ/AWQ support into the isolated runtime. This step downloads Python packages, not models…")
        subprocess.run([str(python), "-m", "pip", "install", "--index-url", "https://pypi.org/simple", "--constraint", str(constraints), "gptqmodel==7.0.0", "optimum==2.3.0"], check=True, timeout=720)
        status("Checking the quantized runtime…")
        subprocess.run([str(python), "-c", "import torch,transformers,gptqmodel,optimum.gptq; from gptqmodel import BACKEND; assert gptqmodel.__version__=='7.0.0'; assert transformers.__version__=='5.5.0'; assert BACKEND.GPTQ_TORCH and BACKEND.AWQ_TORCH"], check=True, timeout=90)
        provenance["ready"] = True
        marker.write_text(json.dumps(provenance, indent=2), encoding="utf-8")
        status("Quantized runtime ready. Choose a GPTQ or AWQ model and Start & connect.", ready=True, python=str(python))
    except Exception as error:
        detail = str(error) if isinstance(error, ValueError) else "Runtime setup could not finish (" + type(error).__name__ + "). Check the setup log, then retry or choose an existing compatible runtime. Your base Python environment was not changed."
        status(detail, error=True)
        print(detail, flush=True)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
