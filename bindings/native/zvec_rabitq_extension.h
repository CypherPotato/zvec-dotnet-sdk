#pragma once

#include "zvec/c_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define OPENINDEXER_ZVEC_RABITQ_EXTENSION_API_VERSION 1
#define OPENINDEXER_ZVEC_RABITQ_ZVEC_COMMIT                                    \
  "ec8a78ee08b14a0b8c94158ffc1de42cd3f97f6d"

ZVEC_EXPORT uint32_t openindexer_zvec_rabitq_extension_api_version(void);
ZVEC_EXPORT const char *openindexer_zvec_rabitq_zvec_commit(void);
ZVEC_EXPORT bool openindexer_zvec_rabitq_is_supported(void);

ZVEC_EXPORT zvec_index_params_t *openindexer_zvec_rabitq_index_params_create(
    zvec_metric_type_t metric_type, int total_bits, int num_clusters, int m,
    int ef_construction, int sample_count);

ZVEC_EXPORT zvec_error_code_t openindexer_zvec_rabitq_index_params_get(
    const zvec_index_params_t *params, zvec_metric_type_t *metric_type,
    int *total_bits, int *num_clusters, int *m, int *ef_construction,
    int *sample_count);

ZVEC_EXPORT void *openindexer_zvec_rabitq_query_params_create(
    int ef, float radius, bool is_linear, bool is_using_refiner);

ZVEC_EXPORT zvec_error_code_t openindexer_zvec_rabitq_query_params_get(
    const void *params, int *ef, float *radius, bool *is_linear,
    bool *is_using_refiner);

ZVEC_EXPORT void openindexer_zvec_rabitq_query_params_destroy(void *params);

ZVEC_EXPORT zvec_error_code_t openindexer_zvec_vector_query_set_rabitq_params(
    zvec_vector_query_t *query, void *params);

#ifdef __cplusplus
}
#endif
