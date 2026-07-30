#pragma once

#include "BassBindings.h"
#include "RenderAheadMixer.h"
#include "yarg_audio.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <vector>

namespace yarg::audio {

class AsioMixerRouter {
public:
    explicit AsioMixerRouter(const yarg_asio_router_config& config);
    ~AsioMixerRouter();
    AsioMixerRouter(const AsioMixerRouter&) = delete;
    AsioMixerRouter& operator=(const AsioMixerRouter&) = delete;

    int initialize() noexcept;
    int attach(std::uint32_t mixer, std::uint32_t bufferMilliseconds) noexcept;
    int prefill(std::uint32_t mixer, std::uint32_t timeoutMilliseconds) noexcept;
    int enableOutput(std::uint32_t firstChannel) noexcept;
    int flush(std::uint32_t mixer) noexcept;
    int getStats(yarg_asio_router_stats& stats) const noexcept;
    int setVolume(float volume) noexcept;

private:
    class BassAudioSource;
    static std::uint32_t CALLBACK outputCallback(int input, std::uint32_t channel,
        void* buffer, std::uint32_t length, void* user) noexcept;
    std::uint32_t processOutput(void* buffer, std::uint32_t length) noexcept;
    void disableOutput() noexcept;
    void updateMinimum(std::uint32_t queued) noexcept;

    const yarg_asio_router_config config_;
    BassBindings bass_;
    std::unique_ptr<RenderAheadMixer> buffered_;
    std::uint32_t bufferedHandle_ = 0;
    std::uint32_t directHandle_ = 0;
    std::uint32_t firstOutputChannel_ = 0;
    std::vector<float> directScratch_;
    std::atomic<float> volume_{1.0f};
    std::atomic<std::uint32_t> state_{YARG_ASIO_ROUTER_CREATED};
    std::atomic<int> lastError_{0};
    std::atomic<bool> outputEnabled_{false};
    std::atomic<std::uint32_t> activeCallbacks_{0};
    std::atomic<std::uint32_t> minimumQueuedFrames_{UINT32_MAX};
    std::atomic<std::uint64_t> consumedSongFrames_{0};
    std::atomic<std::uint64_t> requestedOutputFrames_{0};
    std::atomic<std::uint64_t> underrunFrames_{0};
    std::atomic<std::uint64_t> underrunEvents_{0};
};

} // namespace yarg::audio
