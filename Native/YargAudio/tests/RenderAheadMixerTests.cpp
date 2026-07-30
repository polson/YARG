#include "RenderAheadMixer.h"
#include "Test.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <memory>
#include <thread>
#include <vector>

using namespace std::chrono_literals;
using yarg::audio::IAudioSource;
using yarg::audio::RenderAheadMixer;

namespace {

class SequenceSource final : public IAudioSource {
public:
    explicit SequenceSource(std::size_t channels) : channels_(channels) {}
    bool prepareThread() noexcept override { prepared_.store(true); return true; }
    int read(float* samples, std::size_t frames) noexcept override {
        for (std::size_t frame = 0; frame < frames; ++frame) {
            for (std::size_t channel = 0; channel < channels_; ++channel) {
                samples[frame * channels_ + channel] =
                    static_cast<float>(nextFrame_ * 10 + channel);
            }
            ++nextFrame_;
        }
        return static_cast<int>(frames);
    }
    int lastError() const noexcept override { return 0; }
    bool prepared() const noexcept { return prepared_.load(); }
private:
    std::size_t channels_;
    std::size_t nextFrame_ = 0;
    std::atomic<bool> prepared_{false};
};

class FailingSource final : public IAudioSource {
public:
    bool prepareThread() noexcept override { return true; }
    int read(float*, std::size_t) noexcept override { return -1; }
    int lastError() const noexcept override { return 37; }
};

} // namespace

void runRenderAheadMixerTests() {
    {
        auto source = std::make_unique<SequenceSource>(2);
        auto* sourcePointer = source.get();
        RenderAheadMixer renderer(std::move(source), 48000, 2, 64, 10);
        REQUIRE(renderer.start());
        REQUIRE(renderer.prefill(2s));
        REQUIRE(sourcePointer->prepared());
        REQUIRE(renderer.queuedFrames() >= renderer.targetFrames());

        std::vector<float> output(192 * 2);
        REQUIRE(renderer.consume(output.data(), 192) == 192);
        REQUIRE(output[0] == 0 && output[1] == 1);
        REQUIRE(output[2] == 10 && output[3] == 11);

        const auto deadline = std::chrono::steady_clock::now() + 2s;
        while (renderer.queuedFrames() < renderer.targetFrames() &&
               std::chrono::steady_clock::now() < deadline) {
            std::this_thread::sleep_for(1ms);
        }
        REQUIRE(renderer.queuedFrames() >= renderer.targetFrames());
        renderer.stop();
    }

    {
        RenderAheadMixer renderer(std::make_unique<FailingSource>(), 48000, 2, 64, 10);
        REQUIRE(renderer.start());
        REQUIRE(!renderer.prefill(2s));
        REQUIRE(renderer.failed());
        REQUIRE(renderer.lastError() == 37);
    }
}
