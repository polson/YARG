#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define YARG_AUDIO_CALL __cdecl
#if defined(YARG_AUDIO_BUILD)
#define YARG_AUDIO_API __declspec(dllexport)
#else
#define YARG_AUDIO_API __declspec(dllimport)
#endif
#else
#define YARG_AUDIO_CALL
#define YARG_AUDIO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define YARG_AUDIO_ABI_VERSION 1u

typedef struct yarg_asio_router yarg_asio_router;
typedef struct yarg_gain_dsp yarg_gain_dsp;
typedef struct yarg_freeverb_dsp yarg_freeverb_dsp;
typedef struct yarg_one_shot_stream yarg_one_shot_stream;

typedef enum yarg_audio_result {
    YARG_AUDIO_OK = 0,
    YARG_AUDIO_ERROR_INVALID_ARGUMENT = -1,
    YARG_AUDIO_ERROR_INVALID_STATE = -2,
    YARG_AUDIO_ERROR_UNSUPPORTED = -3,
    YARG_AUDIO_ERROR_DEPENDENCY = -4,
    YARG_AUDIO_ERROR_BASS = -5,
    YARG_AUDIO_ERROR_INTERNAL = -6,
    YARG_AUDIO_ERROR_SOURCE = -7
} yarg_audio_result;

typedef struct yarg_one_shot_config {
    uint32_t size;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t reserved;
    double lead_time;
} yarg_one_shot_config;
typedef enum yarg_asio_router_state {
    YARG_ASIO_ROUTER_CREATED = 0,
    YARG_ASIO_ROUTER_ATTACHED = 1,
    YARG_ASIO_ROUTER_PREFILLING = 2,
    YARG_ASIO_ROUTER_READY = 3,
    YARG_ASIO_ROUTER_RUNNING = 4,
    YARG_ASIO_ROUTER_STARVED = 5,
    YARG_ASIO_ROUTER_SOURCE_FAILED = 6,
    YARG_ASIO_ROUTER_STOPPING = 7,
    YARG_ASIO_ROUTER_STOPPED = 8
} yarg_asio_router_state;

typedef struct yarg_asio_router_config {
    uint32_t size;
    int32_t bass_device_id;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t callback_frames;
} yarg_asio_router_config;

typedef struct yarg_asio_router_stats {
    uint32_t size;
    uint32_t state;
    int32_t last_error;
    uint32_t queued_frames;
    uint32_t minimum_queued_frames;
    uint64_t produced_frames;
    uint64_t consumed_song_frames;
    uint64_t requested_output_frames;
    uint64_t underrun_frames;
    uint64_t underrun_events;
    uint64_t maximum_render_nanoseconds;
} yarg_asio_router_stats;

typedef struct yarg_asio_router_clock {
    uint32_t size;
    uint32_t valid;
    uint32_t sample_rate;
    uint32_t callback_frames;
    int64_t performance_frequency;
    int64_t callback_timestamp;
    uint64_t consumed_song_frames;
    uint64_t requested_output_frames;
    uint32_t queued_frames;
    uint32_t generation;
} yarg_asio_router_clock;


YARG_AUDIO_API uint32_t YARG_AUDIO_CALL yarg_audio_get_abi_version(void);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_gain_dsp_attach(
    uint32_t channel, float initial_gain, int32_t priority,
    yarg_gain_dsp** dsp, int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_gain_dsp_set_gain(
    yarg_gain_dsp* dsp, float gain);
YARG_AUDIO_API void YARG_AUDIO_CALL yarg_gain_dsp_destroy(yarg_gain_dsp* dsp);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_freeverb_dsp_attach(
    uint32_t channel, float dry_mix, float wet_mix, float room_size,
    float damp, float width, int32_t priority,
    yarg_freeverb_dsp** dsp, int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_freeverb_dsp_reset(
    yarg_freeverb_dsp* dsp);
YARG_AUDIO_API void YARG_AUDIO_CALL yarg_freeverb_dsp_destroy(yarg_freeverb_dsp* dsp);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_create(
    const yarg_one_shot_config* config,
    const float* pcm, uint64_t pcm_sample_count,
    const double* schedule, uint64_t schedule_count,
    yarg_one_shot_stream** stream, int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_attach(
    yarg_one_shot_stream* stream, uint32_t mixer,
    double anchor_song_position, float playback_speed, int32_t paused,
    int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_resync(
    yarg_one_shot_stream* stream, uint32_t mixer,
    double anchor_song_position, float playback_speed, int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_resync_ex(
    yarg_one_shot_stream* stream, uint32_t mixer,
    double anchor_song_position, float playback_speed,
    int32_t clear_active_voices, int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_set_paused(
    yarg_one_shot_stream* stream, uint32_t mixer, int32_t paused,
    int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_set_gain(
    yarg_one_shot_stream* stream, float gain);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_detach(
    yarg_one_shot_stream* stream, int32_t* bass_error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_one_shot_stream_destroy(
    yarg_one_shot_stream* stream, int32_t* bass_error);

YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_create(
    const yarg_asio_router_config* config, yarg_asio_router** router);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_attach_mixer(
    yarg_asio_router* router, uint32_t mixer_handle, uint32_t buffer_milliseconds);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_prefill(
    yarg_asio_router* router, uint32_t mixer_handle, uint32_t timeout_milliseconds);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_enable_output(
    yarg_asio_router* router, uint32_t first_asio_channel);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_flush_mixer(
    yarg_asio_router* router, uint32_t mixer_handle);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_set_song_enabled(
    yarg_asio_router* router, int32_t enabled);
YARG_AUDIO_API int64_t YARG_AUDIO_CALL yarg_asio_router_get_source_position(
    yarg_asio_router* router, uint32_t source_handle,
    uint32_t output_latency_frames, int32_t* error);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_get_clock(
    yarg_asio_router* router, yarg_asio_router_clock* clock);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_get_stats(
    yarg_asio_router* router, yarg_asio_router_stats* stats);
YARG_AUDIO_API int32_t YARG_AUDIO_CALL yarg_asio_router_set_volume(
    yarg_asio_router* router, float volume);
YARG_AUDIO_API void YARG_AUDIO_CALL yarg_asio_router_destroy(yarg_asio_router* router);

#ifdef __cplusplus
}
#endif
