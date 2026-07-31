#include "BassAsioBindings.h"
#include "BassCoreBindings.h"
#include "BassMixBindings.h"
#include "Test.h"

#include <cstdint>

using namespace yarg::audio;

namespace {

int YARG_BASS_CALL setDevice(std::uint32_t device) { return device == 7; }
std::uint32_t YARG_BASS_CALL getData(std::uint32_t, void*, std::uint32_t bytes) {
    return bytes;
}
int YARG_BASS_CALL bassError() { return 41; }
std::uint32_t YARG_BASS_CALL setDsp(std::uint32_t, BassDspProc, void*, int) { return 9; }
int YARG_BASS_CALL removeDsp(std::uint32_t, std::uint32_t) { return 1; }
int YARG_BASS_CALL channelLock(std::uint32_t, int) { return 1; }
int YARG_BASS_CALL getInfo(std::uint32_t, BassChannelInfo* info) {
    info->channels = 2;
    return 1;
}
std::uint32_t YARG_BASS_CALL getConfig(std::uint32_t option) { return option + 1; }

std::uint64_t YARG_BASS_CALL getPosition(
    std::uint32_t, std::uint32_t, std::uint32_t delay) {
    return delay + 100;
}

int YARG_BASS_CALL asioEnable(int, std::uint32_t, BassAsioProc, void*) { return 1; }
int YARG_BASS_CALL asioJoin(int, std::uint32_t, std::uint32_t) { return 1; }
int YARG_BASS_CALL asioFormat(int, std::uint32_t, std::uint32_t format) {
    return format == 19;
}
int YARG_BASS_CALL asioRate(int, std::uint32_t, double rate) { return rate == 48000.0; }
int YARG_BASS_CALL asioReset(int, std::uint32_t, std::uint32_t reset) {
    return reset == 1;
}
int YARG_BASS_CALL asioError() { return 3; }

void testCoreBoundary() {
    BassCoreFunctions functions{
        &setDevice, &getData, &bassError, &setDsp, &removeDsp,
        &channelLock, &getInfo, &getConfig
    };
    BassCoreBindings core(functions);
    REQUIRE(core.valid());
    REQUIRE(core.setDevice(7));
    REQUIRE(!core.setDevice(8));
    REQUIRE(core.getData(1, nullptr, 64) == 64);
    REQUIRE(core.error() == 41);
    REQUIRE(core.setDsp(1, nullptr, nullptr, 0) == 9);
    REQUIRE(core.removeDsp(1, 9));
    REQUIRE(core.lockChannel(1, true));
    BassChannelInfo info{};
    REQUIRE(core.getChannelInfo(1, info));
    REQUIRE(info.channels == 2);
    REQUIRE(core.getConfig(7) == 8);

    functions.channelLock = nullptr;
    REQUIRE(!BassCoreBindings(functions).valid());
}

void testAddonBoundaries() {
    BassMixBindings mix(BassMixFunctions{&getPosition});
    REQUIRE(mix.valid());
    REQUIRE(mix.getPosition(1, 25) == 125);
    REQUIRE(!BassMixBindings(BassMixFunctions{}).valid());

    BassAsioFunctions functions{
        &asioEnable, &asioJoin, &asioFormat, &asioRate, &asioReset, &asioError
    };
    BassAsioBindings asio(functions);
    REQUIRE(asio.valid());
    REQUIRE(asio.enable(0, nullptr, nullptr));
    REQUIRE(asio.join(1, 0));
    REQUIRE(asio.setFloat(0));
    REQUIRE(asio.setRate(0, 48000));
    REQUIRE(asio.resetEnable(0));
    REQUIRE(asio.error() == 3);

    functions.channelJoin = nullptr;
    REQUIRE(!BassAsioBindings(functions).valid());
}

} // namespace

void runBassBindingTests() {
    testCoreBoundary();
    testAddonBoundaries();
}
