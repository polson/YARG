#pragma once

#include "BassCoreBindings.h"
#include <cstring>
#include "signalsmith-stretch/signalsmith-stretch.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

namespace yarg::audio {

struct StretchConfig {
    bool adaptiveSidelobeLock = false;
};

// Plays song audio back at any speed and pitch. BASS pulls fixed 256-frame
// blocks through the Signalsmith stretcher; around drum hits we briefly
// swallow extra input so attacks stay snappy, then pay the borrowed frames
// back gradually. Every delivered block is logged in history_ so any output
// point maps back to song time. idealPosition hides the borrow-and-pay-back
// wobble and is the one song-time display should use.
class StretchTempoStream final {
public:
    static constexpr int BLOCK_FRAMES = 256;
    static constexpr float MIN_SPEED = 0.05f;
    static constexpr float MAX_SPEED = 51.0f;

    static std::unique_ptr<StretchTempoStream> create(BassCoreBindings& bass,
        std::uint32_t source, int* bassError,
        const StretchConfig& config = {}) noexcept;
    ~StretchTempoStream();

    std::uint32_t streamHandle() const noexcept { return stream_; }
    std::uint32_t sampleRate() const noexcept { return sampleRate_; }
    std::uint32_t channels() const noexcept { return channels_; }
    int latencyFrames() const noexcept { return stretch_.outputLatency() + BLOCK_FRAMES; }
    int protectedTransients() const noexcept {
        return protectedTransients_.load(std::memory_order_relaxed);
    }
    void setSpeed(float speed, float pitch) noexcept;
    bool position(double outputFrame, double& seconds) const noexcept;
    bool idealPosition(double outputFrame, double& seconds) const noexcept;
    bool getPosition(std::int64_t bytes, double& seconds) noexcept;
    bool getIdealPosition(std::int64_t bytes, double& seconds) noexcept;
    bool flush() noexcept;
    bool destroy() noexcept;

private:
    struct PositionBlock {
        std::uint64_t index = UINT64_MAX;
        double inputFrame = 0;
        int inputFrames = 0;
        double idealInputFrame = 0;
        int idealInputFrames = 0;
    };

    StretchTempoStream(BassCoreBindings& bass, std::uint32_t source,
        const BassChannelInfo& info, const StretchConfig& config);
    void reset() noexcept;
    bool lookup(double outputFrame, double& seconds, bool useIdeal) const noexcept;
    static std::uint32_t YARG_BASS_CALLBACK callback(std::uint32_t stream,
        void* buffer, std::uint32_t length, void* user) noexcept;
    std::uint32_t read(float* output, std::uint32_t frames) noexcept;
    bool readInput(int frames) noexcept;
    bool process() noexcept;
    int nextInputFrames(float speed, int& baseOut) noexcept;
    bool prime() noexcept;

    BassCoreBindings& bass_;
    const std::uint32_t source_;
    const std::uint32_t sampleRate_;
    const std::uint32_t channels_;
    std::uint32_t stream_ = 0;
    signalsmith::stretch::SignalsmithStretch<float> stretch_{0};
    std::vector<float> interleaved_;
    std::vector<float> input_;
    std::vector<float*> inputChannels_;
    std::vector<float> output_;
    std::vector<float*> outputChannels_;
    std::vector<PositionBlock> history_;
    std::atomic<float> speed_{1.0f};
    std::atomic<float> pitch_{1.0f};
    // Position state is queried from the game thread via getPosition() while the
    // read-ahead worker advances it inside the BASS STREAMPROC callback. Scalars
    // are atomic so queries never block on (or stall) realtime audio processing;
    // history entries are published under historyMutex_ (writer stores fields
    // before the entry becomes findable via processedBlocks_).
    mutable std::mutex historyMutex_;
    std::atomic<double> inputRemainder_{0};
    std::atomic<double> strideDebt_{0};
    std::atomic<std::uint64_t> processedBlocks_{0};
    std::atomic<std::uint64_t> inputFrames_{0};
    std::atomic<std::uint64_t> idealInputFrames_{0};
    std::atomic<std::uint64_t> sourceFrames_{0};
    std::atomic<std::uint64_t> deliveredFrames_{0};
    std::atomic<int> outputOffset_{BLOCK_FRAMES};
    std::atomic<bool> primed_{false};
    std::atomic<bool> ended_{false};
    std::atomic<int> protectedTransients_{0};
};

}

struct yarg_stretch_stream {
    std::shared_ptr<yarg::audio::StretchTempoStream> value;
};
