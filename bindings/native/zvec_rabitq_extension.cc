#include "zvec_rabitq_extension.h"

#include <new>

#include "zvec/db/index_params.h"
#include "zvec/db/query.h"
#include "zvec/db/query_params.h"

namespace {

bool is_valid_metric(zvec_metric_type_t metric_type) {
  return metric_type >= ZVEC_METRIC_TYPE_L2 &&
         metric_type <= ZVEC_METRIC_TYPE_MIPSL2;
}

} // namespace

uint32_t openindexer_zvec_rabitq_extension_api_version(void) {
  return OPENINDEXER_ZVEC_RABITQ_EXTENSION_API_VERSION;
}

const char *openindexer_zvec_rabitq_zvec_commit(void) {
  return OPENINDEXER_ZVEC_RABITQ_ZVEC_COMMIT;
}

bool openindexer_zvec_rabitq_is_supported(void) {
#if RABITQ_SUPPORTED
  return true;
#else
  return false;
#endif
}

zvec_index_params_t *openindexer_zvec_rabitq_index_params_create(
    zvec_metric_type_t metric_type, int total_bits, int num_clusters, int m,
    int ef_construction, int sample_count) {
#if !RABITQ_SUPPORTED
  return nullptr;
#else
  if (!is_valid_metric(metric_type) || metric_type == ZVEC_METRIC_TYPE_MIPSL2 ||
      total_bits < 1 || total_bits > 9 || num_clusters < 1 || m < 5 ||
      m > 1024 || ef_construction < 1 || ef_construction > 2048 ||
      sample_count < 0) {
    return nullptr;
  }

  try {
    auto *params = new zvec::HnswRabitqIndexParams(
        static_cast<zvec::MetricType>(metric_type), total_bits, num_clusters, m,
        ef_construction, sample_count);
    return reinterpret_cast<zvec_index_params_t *>(params);
  } catch (...) {
    return nullptr;
  }
#endif
}

zvec_error_code_t openindexer_zvec_rabitq_index_params_get(
    const zvec_index_params_t *params, zvec_metric_type_t *metric_type,
    int *total_bits, int *num_clusters, int *m, int *ef_construction,
    int *sample_count) {
  if (!params || !metric_type || !total_bits || !num_clusters || !m ||
      !ef_construction || !sample_count) {
    return ZVEC_ERROR_INVALID_ARGUMENT;
  }

  auto *base = reinterpret_cast<const zvec::IndexParams *>(params);
  auto *rabitq = dynamic_cast<const zvec::HnswRabitqIndexParams *>(base);
  if (!rabitq || base->type() != zvec::IndexType::HNSW_RABITQ) {
    return ZVEC_ERROR_INVALID_ARGUMENT;
  }

  *metric_type = static_cast<zvec_metric_type_t>(rabitq->metric_type());
  *total_bits = rabitq->total_bits();
  *num_clusters = rabitq->num_clusters();
  *m = rabitq->m();
  *ef_construction = rabitq->ef_construction();
  *sample_count = rabitq->sample_count();
  return ZVEC_OK;
}

void *openindexer_zvec_rabitq_query_params_create(int ef, float radius,
                                                  bool is_linear,
                                                  bool is_using_refiner) {
  if (ef < 1 || ef > 2048) {
    return nullptr;
  }

  try {
    return new zvec::HnswRabitqQueryParams(ef, radius, is_linear,
                                           is_using_refiner);
  } catch (...) {
    return nullptr;
  }
}

zvec_error_code_t
openindexer_zvec_rabitq_query_params_get(const void *params, int *ef,
                                         float *radius, bool *is_linear,
                                         bool *is_using_refiner) {
  if (!params || !ef || !radius || !is_linear || !is_using_refiner) {
    return ZVEC_ERROR_INVALID_ARGUMENT;
  }

  auto *base = reinterpret_cast<const zvec::QueryParams *>(params);
  auto *rabitq = dynamic_cast<const zvec::HnswRabitqQueryParams *>(base);
  if (!rabitq || base->type() != zvec::IndexType::HNSW_RABITQ) {
    return ZVEC_ERROR_INVALID_ARGUMENT;
  }

  *ef = rabitq->ef();
  *radius = rabitq->radius();
  *is_linear = rabitq->is_linear();
  *is_using_refiner = rabitq->is_using_refiner();
  return ZVEC_OK;
}

void openindexer_zvec_rabitq_query_params_destroy(void *params) {
  delete reinterpret_cast<zvec::QueryParams *>(params);
}

zvec_error_code_t
openindexer_zvec_vector_query_set_rabitq_params(zvec_vector_query_t *query,
                                                void *params) {
  if (!query || !params) {
    return ZVEC_ERROR_INVALID_ARGUMENT;
  }

  auto *base = reinterpret_cast<zvec::QueryParams *>(params);
  if (!dynamic_cast<zvec::HnswRabitqQueryParams *>(base) ||
      base->type() != zvec::IndexType::HNSW_RABITQ) {
    return ZVEC_ERROR_INVALID_ARGUMENT;
  }

  auto *query_ptr = reinterpret_cast<zvec::SearchQuery *>(query);
  query_ptr->target_.query_params_.reset(base);
  return ZVEC_OK;
}
