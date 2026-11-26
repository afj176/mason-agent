#include "gpu.hpp"

#include <algorithm>
#include <chrono>
#include <fcntl.h>
#include <poll.h>
#include <sys/wait.h>
#include <unistd.h>

namespace mason::gpu {

namespace {

std::string first_line(const fs::path& file) {
    std::error_code failure;
    if (!fs::is_regular_file(file, failure)) return "";
    try {
        const std::string text = read_file(file);
        return trim(text.substr(0, text.find('\n')));
    } catch (const std::exception&) {
        return "";
    }
}

int64_t whole(const std::string& text, int64_t otherwise = -1) {
    if (text.empty()) return otherwise;
    char* end = nullptr;
    const long long value = std::strtoll(text.c_str(), &end, 10);
    return end && end != text.c_str() ? value : otherwise;
}

/// Runs the driver's tool for at most three seconds and returns what it printed, or nothing when it failed.
std::string run_nvidia() {
    std::string program;
    for (const char* candidate : {"/usr/bin/nvidia-smi", "/usr/local/bin/nvidia-smi"}) {
        if (access(candidate, X_OK) == 0) { program = candidate; break; }
    }
    if (program.empty()) return "";
    int ends[2];
    if (pipe2(ends, O_CLOEXEC) != 0) return "";
    const pid_t child = fork();
    if (child < 0) { close(ends[0]); close(ends[1]); return ""; }
    if (child == 0) {
        const int nothing = open("/dev/null", O_RDWR);
        if (nothing >= 0) { dup2(nothing, 0); dup2(nothing, 2); }
        dup2(ends[1], 1);
        execl(program.c_str(), program.c_str(), "--query-gpu=name,utilization.gpu,memory.total,memory.used", "--format=csv,noheader,nounits", static_cast<char*>(nullptr));
        _exit(127);
    }
    close(ends[1]);
    std::string output;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
    char chunk[2048];
    bool finished = false;
    while (std::chrono::steady_clock::now() < deadline) {
        pollfd waiting = {ends[0], POLLIN, 0};
        if (poll(&waiting, 1, 100) <= 0) continue;
        const ssize_t count = ::read(ends[0], chunk, sizeof chunk);
        if (count <= 0) { finished = true; break; }
        if (output.size() < 65536) output.append(chunk, static_cast<size_t>(count));
    }
    close(ends[0]);
    if (!finished) kill(child, SIGKILL);
    int status = 0;
    waitpid(child, &status, 0);
    return finished && WIFEXITED(status) && WEXITSTATUS(status) == 0 ? output : "";
}

}  // namespace

std::vector<GpuAdapter> parse_nvidia(const std::string& output) {
    std::vector<GpuAdapter> adapters;
    for (const std::string& line : split(output, '\n', false)) {
        const std::vector<std::string> parts = split(line, ',');
        if (parts.size() < 4) continue;
        GpuAdapter adapter;
        adapter.name = trim(parts[0]);
        adapter.busy_percent = static_cast<int>(whole(trim(parts[1])));
        // The driver reports mebibytes.
        adapter.total_bytes = std::max<int64_t>(0, whole(trim(parts[2]), 0)) * 1048576;
        adapter.used_bytes = std::max<int64_t>(0, whole(trim(parts[3]), 0)) * 1048576;
        if (!adapter.name.empty()) adapters.push_back(adapter);
    }
    return adapters;
}

std::vector<GpuAdapter> read_drm(const fs::path& root) {
    std::vector<GpuAdapter> adapters;
    std::error_code failure;
    std::vector<fs::path> cards;
    for (fs::directory_iterator each(root, failure), end; !failure && each != end; each.increment(failure)) {
        const std::string name = each->path().filename().string();
        // card0, card1: the processors themselves. card0-HDMI-A-1 and the like are their outputs.
        if (starts_with(name, "card") && name.find('-') == std::string::npos) cards.push_back(each->path());
    }
    std::sort(cards.begin(), cards.end());
    for (const fs::path& card : cards) {
        const std::string vendor = to_lower(first_line(card / "device" / "vendor"));
        GpuAdapter adapter;
        if (vendor == "0x1002") adapter.name = "AMD graphics";
        else if (vendor == "0x8086") adapter.name = "Intel graphics";
        else continue;  // NVIDIA is read through its own tool; anything else publishes nothing to read here.
        adapter.name += " (" + card.filename().string() + ")";
        adapter.busy_percent = static_cast<int>(whole(first_line(card / "device" / "gpu_busy_percent")));
        adapter.total_bytes = std::max<int64_t>(0, whole(first_line(card / "device" / "mem_info_vram_total"), 0));
        adapter.used_bytes = std::max<int64_t>(0, whole(first_line(card / "device" / "mem_info_vram_used"), 0));
        adapters.push_back(adapter);
    }
    return adapters;
}

GpuSample read(const fs::path& drm_root) {
    GpuSample sample;
    sample.adapters = parse_nvidia(run_nvidia());
    for (const GpuAdapter& adapter : read_drm(drm_root)) sample.adapters.push_back(adapter);
    if (sample.adapters.empty()) sample.note = "No graphics processor that reports its use was found. Models run on the CPU, or the driver says nothing about itself.";
    return sample;
}

std::string describe(const GpuAdapter& adapter) {
    std::string text = adapter.busy_percent >= 0 ? std::to_string(adapter.busy_percent) + "% busy" : "use not reported";
    if (adapter.total_bytes > 0) text += " · " + byte_text(adapter.used_bytes) + " of " + byte_text(adapter.total_bytes) + " in use";
    else text += " · shares the computer's memory";
    return text;
}

}  // namespace mason::gpu
