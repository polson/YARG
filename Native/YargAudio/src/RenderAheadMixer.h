#pragma once

#include "AudioRingBuffer.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

namespace yarg::audio {

class IAudioSource {
public:
    virtual ~IAudioSource() = default;
    virtual bool prepareThread() noexcept = 0;
    virtual int read(float* samples, std::size_t frames) noexcept = 0;
    virtual int lastError() const noexcept = 0;
    virtual std::int64_t position(std::uint32_t sourceHandle,
        std::uint32_t delayBytes) noexcept = 0;
};

class RenderAheadMixer {
public:
    static constexpr std::size_t RenderChunkFrames = 128;

    RenderAheadMixer(std::unique_ptr<IAudioSource> source, std::uint32_t sampleRate,
        std::uint32_t channels, std::uint32_t callbackFrames,
        std::uint32_t bufferMilliseconds);
    ~RenderAheadMixer();
    RenderAheadMixer(const RenderAheadMixer&) = delete;
    RenderAheadMixer& operator=(const RenderAheadMixer&) = delete;

    bool start();
    bool prefill(std::chrono::milliseconds timeout);
    void stop() noexcept;
    bool clear();
    std::size_t consume(float* samples, std::size_t frames) noexcept;
    std::int64_t sourcePosition(std::uint32_t sourceHandle,
        std::uint32_t delayBytes) noexcept;

    std::size_t queuedFrames() const noexcept { return ring_.available(); }
    std::size_t targetFrames() const noexcept { return targetFrames_; }
    bool failed() const noexcept { return failed_.load(std::memory_order_acquire); }
    int lastError() const noexcept { return lastError_.load(std::memory_order_relaxed); }
    std::uint64_t producedFrames() const noexcept { return producedFrames_.load(); }
    std::uint64_t maximumRenderNanoseconds() const noexcept { return maximumRenderNs_.load(); }

private:
    void run() noexcept;
    void updateMaximum(std::uint64_t nanoseconds) noexcept;

    std::unique_ptr<IAudioSource> source_;
    const std::uint32_t channels_;
    const std::size_t targetFrames_;
    const std::size_t lowWatermarkFrames_;
    AudioRingBuffer ring_;
    std::vector<float> scratch_;
    std::mutex mutex_;
    std::mutex sourceMutex_;
    std::condition_variable wake_;
    std::condition_variable prefilled_;
    std::thread worker_;
    bool running_ = false;
    std::atomic<bool> stopping_{false};
    std::atomic<bool> failed_{false};
    std::atomic<int> lastError_{0};
    std::atomic<std::uint64_t> producedFrames_{0};
    std::atomic<std::uint64_t> maximumRenderNs_{0};
};

} // namespace yarg::audio
