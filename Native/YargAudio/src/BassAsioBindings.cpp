#include "BassAsioBindings.h"

namespace yarg::audio {
namespace {

#if defined(_WIN32)
constexpr const char* BassAsioModule = "bassasio.dll";
#elif defined(__APPLE__)
constexpr const char* BassAsioModule = "libbassasio.dylib";
#else
constexpr const char* BassAsioModule = "libbassasio.so";
#endif

template <typename T>
bool bind(const PlatformDynamicLibrary& module, const char* name, T& target) noexcept {
    target = reinterpret_cast<T>(module.symbol(name));
    return target != nullptr;
}

} // namespace

bool BassAsioBindings::load() noexcept {
    module_ = PlatformDynamicLibrary::findLoaded(BassAsioModule);
    if (!module_) module_ = PlatformDynamicLibrary::load(BassAsioModule);
    return module_ &&
        bind(module_, "BASS_ASIO_ChannelEnable", functions_.channelEnable) &&
        bind(module_, "BASS_ASIO_ChannelJoin", functions_.channelJoin) &&
        bind(module_, "BASS_ASIO_ChannelSetFormat", functions_.channelSetFormat) &&
        bind(module_, "BASS_ASIO_ChannelSetRate", functions_.channelSetRate) &&
        bind(module_, "BASS_ASIO_ChannelReset", functions_.channelReset) &&
        bind(module_, "BASS_ASIO_ErrorGetCode", functions_.errorGetCode);
}

bool BassAsioBindings::valid() const noexcept {
    return functions_.channelEnable && functions_.channelJoin &&
        functions_.channelSetFormat && functions_.channelSetRate &&
        functions_.channelReset && functions_.errorGetCode;
}

bool BassAsioBindings::enable(std::uint32_t channel, BassAsioProc proc,
    void* user) const noexcept {
    return functions_.channelEnable &&
        functions_.channelEnable(0, channel, proc, user) != 0;
}

bool BassAsioBindings::join(std::uint32_t channel, std::uint32_t joinTo) const noexcept {
    return functions_.channelJoin && functions_.channelJoin(0, channel, joinTo) != 0;
}

bool BassAsioBindings::setFloat(std::uint32_t channel) const noexcept {
    constexpr std::uint32_t BassAsioFormatFloat = 19;
    return functions_.channelSetFormat &&
        functions_.channelSetFormat(0, channel, BassAsioFormatFloat) != 0;
}

bool BassAsioBindings::setRate(std::uint32_t channel, double rate) const noexcept {
    return functions_.channelSetRate &&
        functions_.channelSetRate(0, channel, rate) != 0;
}

bool BassAsioBindings::resetEnable(std::uint32_t channel) const noexcept {
    constexpr std::uint32_t BassAsioResetEnable = 1;
    return functions_.channelReset &&
        functions_.channelReset(0, channel, BassAsioResetEnable) != 0;
}

int BassAsioBindings::error() const noexcept {
    return functions_.errorGetCode ? functions_.errorGetCode() : -1;
}

} // namespace yarg::audio
