#pragma once

#include <cstdint>

#if defined(_WIN32)
#include <windows.h>
#endif

namespace yarg::audio {

class BassBindings {
public:
    using AsioProc = std::uint32_t(CALLBACK*)(int, std::uint32_t, void*, std::uint32_t, void*);

    BassBindings() = default;
    ~BassBindings();
    BassBindings(const BassBindings&) = delete;
    BassBindings& operator=(const BassBindings&) = delete;

    bool load() noexcept;
    bool setDevice(int device) const noexcept;
    int getData(std::uint32_t channel, void* buffer, std::uint32_t bytes) const noexcept;
    std::int64_t mixerGetPosition(std::uint32_t channel,
        std::uint32_t delayBytes) const noexcept;
    int bassError() const noexcept;

    bool asioEnable(std::uint32_t channel, AsioProc proc, void* user) const noexcept;
    bool asioJoin(std::uint32_t channel, std::uint32_t joinTo) const noexcept;
    bool asioSetFloat(std::uint32_t channel) const noexcept;
    bool asioSetRate(std::uint32_t channel, double rate) const noexcept;
    bool asioResetEnable(std::uint32_t channel) const noexcept;
    int asioError() const noexcept;

private:
    HMODULE bass_ = nullptr;
    HMODULE asio_ = nullptr;
    HMODULE mix_ = nullptr;
    bool ownsBass_ = false;
    bool ownsAsio_ = false;
    bool ownsMix_ = false;

    int (WINAPI* setDevice_)(std::uint32_t) = nullptr;
    std::uint32_t (WINAPI* getData_)(std::uint32_t, void*, std::uint32_t) = nullptr;
    int (WINAPI* bassError_)() = nullptr;
    std::uint64_t (WINAPI* mixerGetPosition_)(
        std::uint32_t, std::uint32_t, std::uint32_t) = nullptr;
    int (WINAPI* asioEnable_)(int, std::uint32_t, AsioProc, void*) = nullptr;
    int (WINAPI* asioJoin_)(int, std::uint32_t, std::uint32_t) = nullptr;
    int (WINAPI* asioSetFormat_)(int, std::uint32_t, std::uint32_t) = nullptr;
    int (WINAPI* asioSetRate_)(int, std::uint32_t, double) = nullptr;
    int (WINAPI* asioReset_)(int, std::uint32_t, std::uint32_t) = nullptr;
    int (WINAPI* asioError_)() = nullptr;
};

} // namespace yarg::audio
