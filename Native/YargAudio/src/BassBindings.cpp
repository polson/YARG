#include "BassBindings.h"

namespace yarg::audio {
namespace {

template <typename T>
bool bind(HMODULE module, const char* name, T& target) noexcept {
    target = reinterpret_cast<T>(GetProcAddress(module, name));
    return target != nullptr;
}

HMODULE acquire(const wchar_t* name, bool& owned) noexcept {
    if (auto module = GetModuleHandleW(name)) {
        owned = false;
        return module;
    }
    owned = true;
    return LoadLibraryW(name);
}

} // namespace

BassBindings::~BassBindings() {
    if (ownsAsio_ && asio_) FreeLibrary(asio_);
    if (ownsMix_ && mix_) FreeLibrary(mix_);
    if (ownsBass_ && bass_) FreeLibrary(bass_);
}

bool BassBindings::load() noexcept {
    bass_ = acquire(L"bass.dll", ownsBass_);
    asio_ = acquire(L"bassasio.dll", ownsAsio_);
    mix_ = acquire(L"bassmix.dll", ownsMix_);
    return bass_ && asio_ && mix_ &&
        bind(bass_, "BASS_SetDevice", setDevice_) &&
        bind(bass_, "BASS_ChannelGetData", getData_) &&
        bind(bass_, "BASS_ErrorGetCode", bassError_) &&
        bind(mix_, "BASS_Mixer_ChannelGetPosition", mixerGetPosition_) &&
        bind(asio_, "BASS_ASIO_ChannelEnable", asioEnable_) &&
        bind(asio_, "BASS_ASIO_ChannelJoin", asioJoin_) &&
        bind(asio_, "BASS_ASIO_ChannelSetFormat", asioSetFormat_) &&
        bind(asio_, "BASS_ASIO_ChannelSetRate", asioSetRate_) &&
        bind(asio_, "BASS_ASIO_ChannelReset", asioReset_) &&
        bind(asio_, "BASS_ASIO_ErrorGetCode", asioError_);
}

std::int64_t BassBindings::mixerGetPosition(std::uint32_t channel,
    std::uint32_t delayBytes) const noexcept {
    constexpr std::uint64_t Error = UINT64_MAX;
    const auto result = mixerGetPosition_(channel, 0, delayBytes);
    return result == Error ? -1 : static_cast<std::int64_t>(result);
}

bool BassBindings::setDevice(int device) const noexcept {
    return setDevice_ && setDevice_(static_cast<std::uint32_t>(device)) != 0;
}

int BassBindings::getData(std::uint32_t channel, void* buffer, std::uint32_t bytes) const noexcept {
    const auto result = getData_(channel, buffer, bytes);
    return result == UINT32_MAX ? -1 : static_cast<int>(result);
}

int BassBindings::bassError() const noexcept { return bassError_ ? bassError_() : -1; }

bool BassBindings::asioEnable(std::uint32_t channel, AsioProc proc, void* user) const noexcept {
    return asioEnable_ && asioEnable_(0, channel, proc, user) != 0;
}

bool BassBindings::asioJoin(std::uint32_t channel, std::uint32_t joinTo) const noexcept {
    return asioJoin_ && asioJoin_(0, channel, joinTo) != 0;
}

bool BassBindings::asioSetFloat(std::uint32_t channel) const noexcept {
    constexpr std::uint32_t BASS_ASIO_FORMAT_FLOAT = 19;
    return asioSetFormat_ && asioSetFormat_(0, channel, BASS_ASIO_FORMAT_FLOAT) != 0;
}

bool BassBindings::asioSetRate(std::uint32_t channel, double rate) const noexcept {
    return asioSetRate_ && asioSetRate_(0, channel, rate) != 0;
}

bool BassBindings::asioResetEnable(std::uint32_t channel) const noexcept {
    constexpr std::uint32_t BASS_ASIO_RESET_ENABLE = 1;
    return asioReset_ && asioReset_(0, channel, BASS_ASIO_RESET_ENABLE) != 0;
}

int BassBindings::asioError() const noexcept { return asioError_ ? asioError_() : -1; }

} // namespace yarg::audio
