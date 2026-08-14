#include <stdio.h>
#include <stdlib.h>

#include "zvec/c_api.h"
#include "zvec_rabitq_extension.h"

int main(void) {
  printf("version=%s architecture=x86_64 extension_api=%u commit=%s "
         "supported=%d\n",
         zvec_get_version(), openindexer_zvec_rabitq_extension_api_version(),
         openindexer_zvec_rabitq_zvec_commit(),
         openindexer_zvec_rabitq_is_supported());

  zvec_index_params_t *generic =
      zvec_index_params_create(ZVEC_INDEX_TYPE_HNSW_RABITQ);
  printf("generic_requested=%u generic_actual=%u handle=%p\n",
         ZVEC_INDEX_TYPE_HNSW_RABITQ, zvec_index_params_get_type(generic),
         (void *)generic);
  zvec_index_params_destroy(generic);

  zvec_index_params_t *params = openindexer_zvec_rabitq_index_params_create(
      ZVEC_METRIC_TYPE_COSINE, 7, 16, 16, 100, 0);
  if (!params) {
    fprintf(stderr, "extension parameter creation failed\n");
    return 2;
  }

  zvec_metric_type_t metric = 0;
  int total_bits = 0, clusters = 0, m = 0, ef_construction = 0,
      sample_count = 0;
  zvec_error_code_t status = openindexer_zvec_rabitq_index_params_get(
      params, &metric, &total_bits, &clusters, &m, &ef_construction,
      &sample_count);
  printf("extension_status=%u requested=%u actual=%u handle=%p metric=%u "
         "total_bits=%d clusters=%d m=%d ef_construction=%d sample_count=%d\n",
         status, ZVEC_INDEX_TYPE_HNSW_RABITQ,
         zvec_index_params_get_type(params), (void *)params, metric, total_bits,
         clusters, m, ef_construction, sample_count);
  if (status != ZVEC_OK ||
      zvec_index_params_get_type(params) != ZVEC_INDEX_TYPE_HNSW_RABITQ) {
    zvec_index_params_destroy(params);
    return 3;
  }
  zvec_index_params_destroy(params);

  void *query =
      openindexer_zvec_rabitq_query_params_create(64, 0.0f, false, true);
  int ef = 0;
  float radius = 0;
  bool linear = false, refiner = false;
  status = openindexer_zvec_rabitq_query_params_get(query, &ef, &radius,
                                                    &linear, &refiner);
  printf("query_status=%u handle=%p ef=%d radius=%g linear=%d refiner=%d\n",
         status, query, ef, radius, linear, refiner);
  openindexer_zvec_rabitq_query_params_destroy(query);
  return status == ZVEC_OK && ef == 64 && refiner ? 0 : 4;
}
