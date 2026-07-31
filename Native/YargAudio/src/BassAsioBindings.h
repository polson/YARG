#pragma once

#include "BassTypes.h"
#include "PlatformDynamicLibrary.h"

#include <cstdint>

namespace yarg::audio {

struct BassAsioFunctions {
    int (YARG_BASS_CALL* channelEnable)(int, std::uint32_t, BassAsioProc, void*) = nullptr;
    int (YARG_BASS_CALL* channelJoin)(int, std::uint32_t, std::uint32_t) = nullptr;
    int (YARG_BASS_CALL* channelSetFormat)(int, std::uint32_t, std::uint32_t) = nullptr;
    int (YARG_BASS_CALL* channelSetRate)(int, std::uint32_t, double) = nullptr;
    int (YARG_BASS_CALL* channelReset)(int, std::uint32_t, std::uint32_t) = nullptr;
    int (YARG_BASS_CALL* errorGetCode)() = nullptr;
};

class BassAsioBindings {
public:
    BassAsioBindings() = default;
    explicit BassAsioBindings(const BassAsioFunctions& functions) noexcept
        : functions_(functions) {}

    bool load() noexcept;
    bool valid() const noexcept;
    bool enable(std::uint32_t channel, BassAsioProc proc, void* user) const noexcept;
    bool join(std::uint32_t channel, std::uint32_t joinTo) const noexcept;
    bool setFloat(std::uint32_t channel) const noexcept;
    bool setRate(std::uint32_t channel, double rate) const noexcept;
    bool resetEnable(std::uint32_t channel) const noexcept;
    int error() const noexcept;

private:
    PlatformDynamicLibrary module_;
    BassAsioFunctions functions_{};
};

} // namespace yarg::audio
