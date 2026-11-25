"""Mason Agent offline Transformers text serving. Uses existing local weights and Python.

Built-in text architectures only; no custom repository code, pickle weights, downloads,
tools, automatic quantization or silent CPU fallback. OpenAI JSON + SSE chat transport.
"""
import argparse
import json
import math
import os
from pathlib import Path
import queue
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1",
                  TORCH_COMPILE_DISABLE="1", GPTQ_TORCH_TRITON_DEQUANT="0", GPTQ_TORCH_CACHE_WEIGHTS="0")
for output in (sys.stdout, sys.stderr):
    if hasattr(output, "reconfigure"):
        output.reconfigure(encoding="utf-8", errors="replace")
FAMILIES = json.loads(Path(__file__).with_name("ModelAdapters.json").read_text(encoding="utf-8"))


def quantization(config):
    q = config.get("quantization_config")
    if "quantization_config" not in config:
        return None
    if not isinstance(q, dict) or not isinstance(q.get("quant_method"), str) or q["quant_method"].lower() not in ("gptq", "awq"):
        raise ValueError("This quantization method needs a dedicated adapter.")
    method = q["quant_method"].lower()
    if type(q.get("bits")) is not int or q["bits"] != 4 or type(q.get("group_size", 128)) is not int or q.get("group_size", 128) not in (-1, 32, 64, 128):
        raise ValueError("Use a supported 4-bit GPTQ/AWQ checkpoint with group size 32, 64, 128 or -1.")
    format_name = q.get("checkpoint_format", q.get("format", "gptq")) if method == "gptq" else q.get("format", q.get("version", "gemm"))
    if not isinstance(format_name, str) or (method == "gptq" and format_name.lower() not in ("gptq", "gptq_v2")) or (method == "awq" and format_name.lower() != "gemm"):
        raise ValueError("This packed quantization format needs a different backend. GPTQ/GPTQ-v2 and AWQ GEMM are supported.")
    if q.get("dynamic") is not None and q.get("dynamic") is not False:
        raise ValueError("Mixed/dynamic quantization requires a dedicated adapter.")
    if config.get("model_type") in ("t5", "bart"):
        raise ValueError("Quantized encoder-decoder models are not supported.")
    return method


def validate_folder(folder):
    config = json.loads((folder / "config.json").read_text(encoding="utf-8"))
    family = config.get("model_type")
    if family not in FAMILIES or FAMILIES[family] not in config.get("architectures", []):
        raise ValueError("This model architecture does not have a managed text adapter.")
    quantization(config)
    if (folder / "adapter_config.json").exists():
        raise ValueError("Adapter-only bundles need a dedicated runtime.")
    if not (folder / "tokenizer_config.json").is_file() or not any((folder / name).is_file() for name in ("tokenizer.json", "tokenizer.model", "spiece.model", "vocab.json")):
        raise ValueError("Tokenizer files are missing. Use the complete local model folder.")
    index = folder / "model.safetensors.index.json"
    if index.is_file():
        if index.stat().st_size > 4 * 1024 * 1024:
            raise ValueError("Weight index exceeds the supported size.")
        weight_map = json.loads(index.read_text(encoding="utf-8")).get("weight_map")
        if not isinstance(weight_map, dict) or not weight_map:
            raise ValueError("Invalid Safetensors shard index.")
        shards = set(weight_map.values())
        if len(shards) > 256 or any(not isinstance(name, str) or name != Path(name).name or "/" in name or "\\" in name or not name.endswith(".safetensors") or not (folder / name).is_file() for name in shards):
            raise ValueError("A Safetensors shard is missing or its index is invalid.")
    elif not (folder / "model.safetensors").is_file():
        raise ValueError("model.safetensors or a complete shard index is required.")
    return config


def validate_request(body, alias, count_only=False):
    if count_only and (not isinstance(body, dict) or set(body) != {"model", "messages"}):
        raise ValueError("Token counting accepts only model and messages.")
    if not isinstance(body, dict) or set(body) - {"model", "messages", "max_tokens", "temperature", "stream", "chat_template_kwargs", "reasoning_effort"} or body.get("model") != alias:
        raise ValueError("Invalid model or unsupported request fields.")
    if "chat_template_kwargs" in body and body["chat_template_kwargs"] != {"enable_thinking": False} or "reasoning_effort" in body and body["reasoning_effort"] != "none":
        raise ValueError("This text adapter does not implement reasoning controls.")
    messages = body.get("messages")
    if not isinstance(messages, list) or not 1 <= len(messages) <= 400:
        raise ValueError("Supply 1–400 text messages.")
    if any(not isinstance(m, dict) or set(m) != {"role", "content"} or m["role"] not in ("system", "user", "assistant") or not isinstance(m["content"], str) or
           (not m["content"].strip() and not (count_only and i == len(messages) - 1 and m["role"] == "user" and m["content"] == "")) for i, m in enumerate(messages)):
        raise ValueError("Only system, user and assistant text messages are supported.")
    if sum(len(m["content"]) for m in messages) > 4 * 1024 * 1024:
        raise ValueError("Conversation exceeds the 4 Mi-character resource bound; nothing is silently removed.")
    tokens, temperature, stream = body.get("max_tokens"), body.get("temperature", 0.2), body.get("stream", False)
    if ("max_tokens" in body and (type(tokens) is not int or not 1 <= tokens <= 2147483647)) or type(temperature) not in (int, float) or not math.isfinite(temperature) or not 0 <= temperature <= 2 or type(stream) is not bool:
        raise ValueError("Use positive whole output tokens, temperature 0–2 and a boolean stream flag. Prompt plus output must fit the loaded context.")
    return messages, tokens, temperature, stream


def encode_prompt(tokenizer, messages, has_template, return_tensors=None):
    """One untruncated formatting path for both the context meter and inference."""
    if has_template:
        prompt = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True, enable_thinking=False)
    else:
        prompt = "\n\n".join(m["role"].capitalize() + ": " + m["content"] for m in messages) + "\n\nAssistant:"
    return tokenizer(prompt, add_special_tokens=not has_template, truncation=False, padding=False, return_tensors=return_tensors)


def resolve_output_budget(requested, input_tokens, context, encoder_decoder=False, output_context=None):
    """Automatic uses tokenizer-measured space; an explicit limit is never silently changed."""
    available = (output_context or context) if encoder_decoder else max(0, context - input_tokens)
    if input_tokens > context or available == 0 or requested is not None and requested > available:
        window = "in the separate decoder window" if encoder_decoder else "in the shared context window"
        raise ValueError(f"Prompt uses {input_tokens} of {context} input tokens; output capacity is {available} tokens {window}. Shorten the conversation, use Automatic output, or load a larger context. Nothing was truncated.")
    return available if requested is None else requested


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--device", choices=("cuda", "cpu"), required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--context", type=int, default=4096)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--adapter-dir")
    args = parser.parse_args()
    manifest = Path(args.manifest)
    metadata = {"modelId": args.model_id, "offline": True, "device": args.device}

    def status(message, **values):
        metadata.update(status=message, **values)
        manifest.parent.mkdir(parents=True, exist_ok=True)
        temporary = manifest.with_suffix(".tmp")
        temporary.write_text(json.dumps(metadata, indent=2), encoding="utf-8")
        temporary.replace(manifest)

    try:
        if not 1024 <= args.port <= 65535 or not args.model_id.strip() or not 64 <= args.context <= 2147483647:
            raise ValueError("Use a valid local port and model ID.")
        folder = Path(args.model_dir).resolve(strict=True)
        status("Checking local files and Python setup…")
        config_data = validate_folder(folder)
        quant = quantization(config_data)
        try:
            import torch
            import transformers
            from transformers import AutoConfig, AutoModelForCausalLM, AutoModelForSeq2SeqLM, AutoTokenizer, TextIteratorStreamer, StoppingCriteria, StoppingCriteriaList
        except ImportError as error:
            raise ValueError("Choose a Python environment with PyTorch, Transformers and this tokenizer's dependencies already installed in Models → Setup.") from error
        if args.device == "cuda" and not torch.cuda.is_available():
            raise ValueError("CUDA is unavailable in this Python environment. Choose CPU or select a CUDA-enabled Python runtime in Setup.")
        torch.set_num_threads(4)
        if quant:
            try:
                import gptqmodel
                import optimum.gptq
                from packaging.version import Version
                if Version(gptqmodel.__version__) != Version("7.0.0"):
                    raise ImportError("Unvalidated quantized runtime version")
            except (ImportError, OSError) as error:
                raise ValueError("Quantized runtime is not ready. Open Models → Setup → Quantized models to set it up or choose a compatible runtime.") from error
        config = AutoConfig.from_pretrained(folder, local_files_only=True, trust_remote_code=False)
        if quant:
            # Checkpoint metadata describes packing, never the kernel to execute. Select the Windows-safe Torch backend.
            config.quantization_config = dict(config_data["quantization_config"], quant_method=quant, backend="torch" if quant == "gptq" else "torch_awq")
        encoder_decoder = bool(config.is_encoder_decoder)
        model_class = AutoModelForSeq2SeqLM if encoder_decoder else AutoModelForCausalLM
        if type(config) not in model_class._model_mapping:
            raise ValueError("This Transformers version does not support the model. Choose a compatible Python environment in Setup.")
        tokenizer = AutoTokenizer.from_pretrained(folder, local_files_only=True, trust_remote_code=False)
        # Validate template without loading weights. Base models use an explicit text transcript.
        has_template = bool(tokenizer.chat_template)
        if has_template:
            tokenizer.apply_chat_template([{"role": "user", "content": "Hello"}], tokenize=False, add_generation_prompt=True, enable_thinking=False)
        context = min(args.context, int(config_data.get("max_position_embeddings", config_data.get("n_positions", 4096))), int(tokenizer.model_max_length))
        if context < 64:
            raise ValueError("Model context capacity is too small for this text adapter.")
        status("Setup ready. Loading starts only when requested.", family=config_data["model_type"], architecture=FAMILIES[config_data["model_type"]], quantization=quant, contextTokens=context, promptFormat="Model chat template" if has_template else "Plain role transcript", torch=torch.__version__, transformers=transformers.__version__)
        if args.check_only:
            return
        status("Loading local weights into GPU memory…" if args.device == "cuda" else "Loading local weights into system memory…")
        dtype = (torch.float16 if args.device == "cuda" else torch.bfloat16) if quant else (torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16) if args.device == "cuda" else torch.float32
        # Pass a fresh generation config later: repository custom_generate and generation overrides are not used.
        concrete = model_class._model_mapping[type(config)]
        attention = "sdpa" if getattr(concrete, "_supports_sdpa", False) else "eager"
        placement = {"device_map": {"": args.device}} if quant else {}
        model, loading = model_class.from_pretrained(folder, config=config, local_files_only=True, trust_remote_code=False, use_safetensors=True, dtype=dtype, attn_implementation=attention, output_loading_info=True, **placement)
        if loading.get("missing_keys") or loading.get("mismatched_keys") or loading.get("error_msgs"):
            raise ValueError("Weights do not completely match this text architecture. Use a complete matching model checkpoint; missing parameters are not initialized randomly.")
        if args.adapter_dir:
            if quant:
                raise ValueError("Trained adapters use their original unquantized local base.")
            adapter = Path(args.adapter_dir).resolve()
            if not (adapter / "adapter_model.safetensors").is_file() or not (adapter / "adapter_config.json").is_file():
                raise ValueError("Local trained adapter files are missing.")
            from peft import PeftModel
            model = PeftModel.from_pretrained(model, str(adapter), is_trainable=False, local_files_only=True)
        model = model.eval() if quant else model.eval().to(args.device)
        packed = sum(1 for module in model.modules() if hasattr(module, "qweight"))
        if quant and packed == 0:
            raise ValueError("The checkpoint did not load any packed quantized layers. It will not be served as a quantized model.")
        status("Ready · text generation", dtype=str(dtype), packedLayers=packed, backend="PyTorch packed weights" if quant else "Transformers + local LoRA" if args.adapter_dir else "Transformers", adapterDirectory=args.adapter_dir, endpoint=f"http://127.0.0.1:{args.port}/")
    except Exception as error:
        if isinstance(error, ValueError):
            message = str(error)[:1000]
        elif type(error).__name__ == "OutOfMemoryError":
            message = "Not enough GPU memory. Stop another model, choose a smaller model or select CPU, then retry."
        else:
            message = "Local model setup failed (" + type(error).__name__ + "). Check complete model files and the selected Python environment; no model was downloaded."
        status(message, error=True)
        print(message, flush=True)
        raise SystemExit(1)

    inference_lock = threading.Lock()
    tokenizer_lock = threading.RLock()  # Fast tokenizers mutate padding/truncation configuration during encode.
    output_context = context - 1 if encoder_decoder else None  # Decoder start token occupies one position.

    class LockedStreamer(TextIteratorStreamer):
        def put(self, value):
            with tokenizer_lock:
                super().put(value)

        def end(self):
            with tokenizer_lock:
                super().end()

    class Handler(BaseHTTPRequestHandler):
        def setup(self):
            super().setup()
            self.connection.settimeout(10)

        def log_message(self, *_):
            pass  # No prompts or response bodies in access logs.

        def allowed(self):
            if self.headers.get("Host") not in (f"127.0.0.1:{args.port}", f"localhost:{args.port}") or self.headers.get("Origin"):
                self.send_json(403, {"error": "Direct loopback clients only"})
                return False
            return True

        def send_json(self, code, body):
            data = json.dumps(body, allow_nan=False).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            self.wfile.write(data)

        def do_GET(self):
            if not self.allowed():
                return
            if self.path == "/v1/models":
                self.send_json(200, {"data": [{"id": args.model_id, "object": "model", "owned_by": "mason-transformers", "meta": {"n_ctx": context, "n_ctx_train": int(config_data.get("max_position_embeddings", config_data.get("n_positions", 4096))), "chat_token_count": "v1", "n_ctx_output": output_context}}]})
            elif self.path == "/health":
                self.send_json(200, {"status": "busy" if inference_lock.locked() else "ok", "device": args.device})
            else:
                self.send_json(404, {"error": "Text generation only"})

        def do_POST(self):
            if not self.allowed():
                return
            count_only = self.path == "/v1/chat/token-count"
            if self.path != "/v1/chat/completions" and not count_only:
                self.send_json(404, {"error": "This model serves text generation only"})
                return
            acquired = False
            worker = None
            stopped = threading.Event()
            streaming = False
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 32 * 1024 * 1024 or self.headers.get("Transfer-Encoding") or self.headers.get_content_type() != "application/json":
                    raise ValueError("Invalid request length or content type.")
                raw = self.rfile.read(length)
                if len(raw) != length:
                    raise ValueError("Incomplete request.")
                messages, budget, temperature, streaming = validate_request(json.loads(raw), args.model_id, count_only)
                if count_only:
                    with tokenizer_lock:
                        input_tokens = len(encode_prompt(tokenizer, messages, has_template)["input_ids"])
                    # Over-window counts are intentional: the compactor must be able to measure oversized history.
                    self.send_json(200, {"model": args.model_id, "prompt_tokens": input_tokens, "context_tokens": context, "output_context_tokens": output_context})
                    return
                if not inference_lock.acquire(blocking=False):
                    self.send_json(429, {"error": "Model is busy. Wait for the current request to finish."})
                    return
                acquired = True
                with tokenizer_lock:
                    encoded = encode_prompt(tokenizer, messages, has_template, "pt")
                input_tokens = encoded["input_ids"].shape[1]
                try:
                    budget = resolve_output_budget(budget, input_tokens, context, encoder_decoder, output_context)
                except ValueError as error:
                    self.send_json(422, {"error": str(error)})
                    return
                encoded = {key: value.to(args.device) for key, value in encoded.items()}
                deadline = time.monotonic() + 120 * 60  # Client cancellation/deadline closes SSE; retain a server resource ceiling.

                class BoundedStop(StoppingCriteria):
                    def __call__(self, *_args, **_kwargs):
                        return stopped.is_set() or time.monotonic() > deadline

                generation = transformers.GenerationConfig(max_new_tokens=budget, do_sample=temperature > 0, eos_token_id=model.generation_config.eos_token_id, pad_token_id=tokenizer.pad_token_id if tokenizer.pad_token_id is not None else tokenizer.eos_token_id, bos_token_id=model.generation_config.bos_token_id, decoder_start_token_id=model.generation_config.decoder_start_token_id)
                if temperature > 0:
                    generation.temperature = temperature
                kwargs = dict(**encoded, generation_config=generation, stopping_criteria=StoppingCriteriaList([BoundedStop()]))

                def completion(result):
                    token_ids = result[0] if encoder_decoder else result[0][input_tokens:]
                    eos = generation.eos_token_id
                    eos_ids = eos if isinstance(eos, list) else [eos]
                    reason = "stop" if len(token_ids) and int(token_ids[-1]) in eos_ids else "length"
                    with tokenizer_lock:
                        return tokenizer.decode(token_ids, skip_special_tokens=True), reason

                def usage(result):
                    generated = len(result[0]) - (1 if encoder_decoder else input_tokens)
                    return {"prompt_tokens": input_tokens, "completion_tokens": generated, "total_tokens": input_tokens + generated}

                if not streaming:
                    with torch.inference_mode():
                        result = model.generate(**kwargs)
                    answer, finish = completion(result)
                    self.send_json(200, {"model": args.model_id, "choices": [{"index": 0, "message": {"role": "assistant", "content": answer}, "finish_reason": finish}], "usage": usage(result)})
                    return

                streamer = LockedStreamer(tokenizer, skip_prompt=True, skip_special_tokens=True, timeout=0.5)
                result_box = {}

                def generate():
                    try:
                        with torch.inference_mode():
                            result_box["result"] = model.generate(**kwargs, streamer=streamer)
                    except Exception as error:
                        result_box["error"] = type(error).__name__
                    finally:
                        inference_lock.release()

                thread = threading.Thread(target=generate, daemon=True)
                thread.start()
                worker = thread
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Connection", "close")
                self.end_headers()
                self.close_connection = True

                def event(value):
                    self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode("utf-8"))
                    self.wfile.flush()

                while True:
                    try:
                        fragment = next(streamer)
                        if fragment:
                            event({"model": args.model_id, "choices": [{"index": 0, "delta": {"content": fragment}}]})
                    except StopIteration:
                        break
                    except queue.Empty:
                        if not worker.is_alive():
                            break
                        if time.monotonic() > deadline:
                            stopped.set()
                        # A heartbeat also detects disconnected clients while generation is quiet.
                        self.wfile.write(b": waiting\n\n")
                        self.wfile.flush()
                worker.join(timeout=2)
                if "result" not in result_box:
                    event({"error": "Local generation failed or exceeded its deadline. Check GPU capacity."})
                    return
                _, finish = completion(result_box["result"])
                event({"model": args.model_id, "choices": [{"index": 0, "delta": {}, "finish_reason": finish}], "usage": usage(result_box["result"])})
                self.wfile.write(b"data: [DONE]\n\n")
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError, TimeoutError):
                stopped.set()
            except (ValueError, TypeError, KeyError, UnicodeDecodeError):
                if worker is None:
                    self.send_json(400, {"error": "Invalid chat request or incompatible chat template. Use text messages within the supported limits."})
            except Exception as error:
                print("Generation failed: " + type(error).__name__, flush=True)
                if worker is None:
                    self.send_json(500, {"error": "Local generation failed. Check available GPU memory and model compatibility."})
            finally:
                stopped.set()
                if acquired and worker is None:
                    inference_lock.release()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.daemon_threads = True
    server.serve_forever(poll_interval=0.25)


if __name__ == "__main__":
    main()
