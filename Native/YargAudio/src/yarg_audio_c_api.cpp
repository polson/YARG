#include "BassCoreBindings.h"
#include "BassMixBindings.h"
#include "dsp/FreeverbDsp.h"
#include "dsp/GainDsp.h"
#include "one_shot/NativeOneShotStream.h"
#include "yarg_audio.h"

#include <cmath>
#include <limits>
#include <memory>
#include <cstdint>

static_assert(sizeof(yarg_asio_router_config) == 20);
static_assert(sizeof(yarg_asio_router_stats) == 72);
static_assert(sizeof(yarg_asio_router_clock) == 56);
static_assert(sizeof(yarg_one_shot_config) == 24);
static_assert(sizeof(int32_t) == sizeof(int));

#if defined(_WIN32)
#include "AsioMixerRouter.h"

#include <memory>

struct yarg_asio_router {
    explicit yarg_asio_router(const yarg_asio_router_config& config) : value(config) {}
    yarg::audio::AsioMixerRouter value;
};
#endif

struct yarg_one_shot_stream {
    std::unique_ptr<yarg::audio::NativeOneShotStream> value;
};

namespace {

#if defined(_WIN32)
bool validConfig(const yarg_asio_router_config* config) {
    return config && config->size >= sizeof(yarg_asio_router_config) &&
        config->sample_rate > 0 && config->channels == 2 &&
        config->callback_frames > 0;
}
#endif

yarg::audio::BassCoreBindings& coreBassBindings() noexcept {
    static yarg::audio::BassCoreBindings bindings;
    static const bool loaded = bindings.load();
    (void) loaded;
    return bindings;
}

yarg::audio::BassMixBindings& mixBassBindings() noexcept {
    static yarg::audio::BassMixBindings bindings;
    static const bool loaded = bindings.load();
    (void) loaded;
    return bindings;
}

bool validOneShotConfig(const yarg_one_shot_config* config) noexcept {
    return config && config->size >= sizeof(yarg_one_shot_config) &&
        config->sample_rate > 0 && config->channels > 0 &&
        std::isfinite(config->lead_time) && config->lead_time >= 0;
}

bool validOneShotCounts(std::uint64_t pcmSampleCount,
    std::uint64_t scheduleCount) noexcept {
    constexpr auto maximum = std::numeric_limits<std::size_t>::max();
    return pcmSampleCount <= maximum && scheduleCount <= maximum &&
        pcmSampleCount <= maximum / sizeof(float) &&
        scheduleCount <= maximum / sizeof(double);
}

bool validOneShotSchedule(const double* schedule, std::size_t count) noexcept {
    if (count > 0 && !schedule) return false;
    for (std::size_t i = 0; i < count; ++i) {
        if (!std::isfinite(schedule[i])) return false;
        if (i > 0 && schedule[i] < schedule[i - 1]) return false;
    }
    return true;
}

void storeBassError(int32_t* target, int error) noexcept {
    if (target) *target = static_cast<int32_t>(error);
}

} // namespace

uint32_t YARG_AUDIO_CALL yarg_audio_get_abi_version(void) {
    return YARG_AUDIO_ABI_VERSION;
}

int32_t YARG_AUDIO_CALL yarg_gain_dsp_attach(uint32_t channel,
    float initial_gain, int32_t priority, yarg_gain_dsp** dsp, int32_t* bass_error) {
    return yarg::audio::gainDspAttach(coreBassBindings(), channel, initial_gain,
        priority, dsp, bass_error);
}

int32_t YARG_AUDIO_CALL yarg_gain_dsp_set_gain(yarg_gain_dsp* dsp, float gain) {
    return yarg::audio::gainDspSetGain(dsp, gain);
}

void YARG_AUDIO_CALL yarg_gain_dsp_destroy(yarg_gain_dsp* dsp) {
    (void) yarg::audio::gainDspDestroy(dsp);
}

int32_t YARG_AUDIO_CALL yarg_freeverb_dsp_attach(uint32_t channel,
    float dry_mix, float wet_mix, float room_size, float damp, float width,
    int32_t priority, yarg_freeverb_dsp** dsp, int32_t* bass_error) {
    return yarg::audio::freeverbDspAttach(coreBassBindings(), channel, dry_mix,
        wet_mix, room_size, damp, width, priority, dsp, bass_error);
}

int32_t YARG_AUDIO_CALL yarg_freeverb_dsp_reset(yarg_freeverb_dsp* dsp) {
    return yarg::audio::freeverbDspRequestReset(dsp);
}

void YARG_AUDIO_CALL yarg_freeverb_dsp_destroy(yarg_freeverb_dsp* dsp) {
    (void) yarg::audio::freeverbDspDestroy(dsp);
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_create(
    const yarg_one_shot_config* config, const float* pcm,
    uint64_t pcmSampleCount, const double* schedule, uint64_t scheduleCount,
    yarg_one_shot_stream** stream, int32_t* bassError) {
    if (stream) *stream = nullptr;
    if (bassError) *bassError = 0;
    if (!stream || !validOneShotConfig(config) || !validOneShotCounts(
        pcmSampleCount, scheduleCount) || !pcm || pcmSampleCount == 0 ||
        pcmSampleCount % config->channels != 0 ||
        !validOneShotSchedule(schedule, static_cast<std::size_t>(scheduleCount))) {
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    }

    auto& core = coreBassBindings();
    auto& mix = mixBassBindings();
    if (!core.oneShotValid() || !mix.oneShotValid())
        return YARG_AUDIO_ERROR_DEPENDENCY;

    int error = 0;
    auto value = yarg::audio::NativeOneShotStream::create(core, mix,
        config->sample_rate, config->channels, pcm,
        static_cast<std::size_t>(pcmSampleCount), schedule,
        static_cast<std::size_t>(scheduleCount), config->lead_time, &error);
    if (!value) {
        storeBassError(bassError, error);
        return error != 0 ? YARG_AUDIO_ERROR_BASS : YARG_AUDIO_ERROR_SOURCE;
    }

    try {
        auto result = std::make_unique<yarg_one_shot_stream>();
        result->value = std::move(value);
        *stream = result.release();
        return YARG_AUDIO_OK;
    } catch (...) {
        int cleanupError = 0;
        if (value && !value->destroy(&cleanupError)) value.release();
        return YARG_AUDIO_ERROR_INTERNAL;
    }
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_attach(
    yarg_one_shot_stream* stream, uint32_t mixer,
    double anchorSongPosition, float playbackSpeed, int32_t paused,
    int32_t* bassError) {
    if (bassError) *bassError = 0;
    if (!stream || !stream->value) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    int error = 0;
    const int result = stream->value->attach(mixer, anchorSongPosition,
        playbackSpeed, paused != 0, &error);
    storeBassError(bassError, error);
    return result;
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_resync(
    yarg_one_shot_stream* stream, uint32_t mixer,
    double anchorSongPosition, float playbackSpeed, int32_t* bassError) {
    return yarg_one_shot_stream_resync_ex(stream, mixer, anchorSongPosition,
        playbackSpeed, 1, bassError);
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_resync_ex(
    yarg_one_shot_stream* stream, uint32_t mixer,
    double anchorSongPosition, float playbackSpeed, int32_t clearActiveVoices,
    int32_t* bassError) {
    if (bassError) *bassError = 0;
    if (!stream || !stream->value) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    int error = 0;
    const int result = stream->value->resync(mixer, anchorSongPosition,
        playbackSpeed, clearActiveVoices != 0, &error);
    storeBassError(bassError, error);
    return result;
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_set_paused(
    yarg_one_shot_stream* stream, uint32_t mixer, int32_t paused,
    int32_t* bassError) {
    if (bassError) *bassError = 0;
    if (!stream || !stream->value) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    int error = 0;
    const int result = stream->value->setPaused(mixer, paused != 0, &error);
    storeBassError(bassError, error);
    return result;
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_set_gain(
    yarg_one_shot_stream* stream, float gain) {
    if (!stream || !stream->value) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    return stream->value->setGain(gain);
}

int64_t YARG_AUDIO_CALL yarg_asio_router_get_source_position(yarg_asio_router* router,
    uint32_t source, uint32_t outputLatencyFrames, int32_t* error) {
    if (!router || !error) return -1;
    int result = YARG_AUDIO_OK;
    const auto position = router->value.getSourcePosition(
        source, outputLatencyFrames, result);
    *error = result;
    return position;
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_detach(
    yarg_one_shot_stream* stream, int32_t* bassError) {
    if (bassError) *bassError = 0;
    if (!stream || !stream->value) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    int error = 0;
    const int result = stream->value->detach(&error);
    storeBassError(bassError, error);
    return result;
}

int32_t YARG_AUDIO_CALL yarg_one_shot_stream_destroy(
    yarg_one_shot_stream* stream, int32_t* bassError) {
    if (bassError) *bassError = 0;
    if (!stream || !stream->value) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    int error = 0;
    if (!stream->value->destroy(&error)) {
        storeBassError(bassError, error);
        return error != 0 ? YARG_AUDIO_ERROR_BASS : YARG_AUDIO_ERROR_INVALID_STATE;
    }
    storeBassError(bassError, error);
    delete stream;
    return YARG_AUDIO_OK;
}

#if defined(_WIN32)
int32_t YARG_AUDIO_CALL yarg_asio_router_create(
    const yarg_asio_router_config* config, yarg_asio_router** router) {
    if (!router || !validConfig(config)) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    *router = nullptr;
    try {
        auto value = std::make_unique<yarg_asio_router>(*config);
        const int result = value->value.initialize();
        if (result != YARG_AUDIO_OK) return result;
        *router = value.release();
        return YARG_AUDIO_OK;
    } catch (...) {
        return YARG_AUDIO_ERROR_INTERNAL;
    }
}

int32_t YARG_AUDIO_CALL yarg_asio_router_attach_mixer(yarg_asio_router* router,
    uint32_t mixer_handle, uint32_t buffer_milliseconds) {
    return router ? router->value.attach(mixer_handle, buffer_milliseconds)
                  : YARG_AUDIO_ERROR_INVALID_ARGUMENT;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_prefill(yarg_asio_router* router,
    uint32_t mixer_handle, uint32_t timeout_milliseconds) {
    return router ? router->value.prefill(mixer_handle, timeout_milliseconds)
                  : YARG_AUDIO_ERROR_INVALID_ARGUMENT;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_enable_output(yarg_asio_router* router,
    uint32_t first_asio_channel) {
    return router ? router->value.enableOutput(first_asio_channel)
                  : YARG_AUDIO_ERROR_INVALID_ARGUMENT;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_flush_mixer(yarg_asio_router* router,
    uint32_t mixer_handle) {
    return router ? router->value.flush(mixer_handle)
                  : YARG_AUDIO_ERROR_INVALID_ARGUMENT;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_set_song_enabled(yarg_asio_router* router,
    int32_t enabled) {
    return router ? router->value.setSongEnabled(enabled != 0)
                  : YARG_AUDIO_ERROR_INVALID_ARGUMENT;
}

int64_t YARG_AUDIO_CALL yarg_asio_router_get_source_position(yarg_asio_router* router,
    uint32_t source, uint32_t outputLatencyFrames, int32_t* error) {
    if (!router || !error) return -1;
    int result = YARG_AUDIO_OK;
    const auto position = router->value.getSourcePosition(
        source, outputLatencyFrames, result);
    *error = result;
    return position;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_get_clock(yarg_asio_router* router,
    yarg_asio_router_clock* clock) {
    if (!router || !clock || clock->size < sizeof(yarg_asio_router_clock))
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    return router->value.getClock(*clock);
}

int32_t YARG_AUDIO_CALL yarg_asio_router_get_stats(yarg_asio_router* router,
    yarg_asio_router_stats* stats) {
    if (!router || !stats || stats->size < sizeof(yarg_asio_router_stats))
        return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    return router->value.getStats(*stats);
}

int32_t YARG_AUDIO_CALL yarg_asio_router_set_volume(
    yarg_asio_router* router, float volume) {
    return router ? router->value.setVolume(volume) : YARG_AUDIO_ERROR_INVALID_ARGUMENT;
}

void YARG_AUDIO_CALL yarg_asio_router_destroy(yarg_asio_router* router) {
    delete router;
}
#else
int32_t YARG_AUDIO_CALL yarg_asio_router_create(
    const yarg_asio_router_config*, yarg_asio_router** router) {
    if (!router) return YARG_AUDIO_ERROR_INVALID_ARGUMENT;
    *router = nullptr;
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_attach_mixer(
    yarg_asio_router*, uint32_t, uint32_t) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_prefill(
    yarg_asio_router*, uint32_t, uint32_t) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_enable_output(
    yarg_asio_router*, uint32_t) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_flush_mixer(
    yarg_asio_router*, uint32_t) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_set_song_enabled(
    yarg_asio_router*, int32_t) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int64_t YARG_AUDIO_CALL yarg_asio_router_get_source_position(
    yarg_asio_router*, uint32_t, uint32_t, int32_t* error) {
    if (error) *error = YARG_AUDIO_ERROR_UNSUPPORTED;
    return -1;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_get_clock(
    yarg_asio_router*, yarg_asio_router_clock*) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_get_stats(
    yarg_asio_router*, yarg_asio_router_stats*) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

int32_t YARG_AUDIO_CALL yarg_asio_router_set_volume(yarg_asio_router*, float) {
    return YARG_AUDIO_ERROR_UNSUPPORTED;
}

void YARG_AUDIO_CALL yarg_asio_router_destroy(yarg_asio_router*) {
}
#endif
