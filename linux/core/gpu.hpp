// A live reading of the graphics processors on this computer: how busy each is and how much of its memory is in
// use. NVIDIA cards are read through the driver's nvidia-smi; AMD and Intel ones from what the kernel publishes
// under /sys/class/drm. Nothing is installed or changed to take a reading.
#pragma once

#include "util.hpp"

namespace mason {

struct GpuAdapter {
    std::string name;
    int busy_percent = -1;     ///< -1 when the driver does not say
    int64_t total_bytes = 0;   ///< 0 when the processor shares the computer's memory
    int64_t used_bytes = 0;
};

struct GpuSample {
    std::vector<GpuAdapter> adapters;
    /// Why there is nothing to show, when there is nothing.
    std::string note;
};

namespace gpu {
/// What nvidia-smi printed for name, utilization, total and used memory, as adapters.
std::vector<GpuAdapter> parse_nvidia(const std::string& output);
/// The AMD and Intel processors the kernel lists under `root` (normally /sys/class/drm).
std::vector<GpuAdapter> read_drm(const fs::path& root);
GpuSample read(const fs::path& drm_root = "/sys/class/drm");
/// One adapter as a line for a person: its name, how busy, and its memory.
std::string describe(const GpuAdapter& adapter);
}  // namespace gpu

}  // namespace mason
