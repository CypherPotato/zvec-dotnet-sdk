# Third-party notices

The `OpenIndexer.ZVec` NuGet package contains precompiled native ZVec runtimes for Windows x64, Linux x64, Linux ARM64, and macOS ARM64.

## ZVec

- Project: https://github.com/alibaba/zvec
- Source revision: `ec8a78ee08b14a0b8c94158ffc1de42cd3f97f6d`
- License: Apache License 2.0
- Included files: `licenses/ZVec-LICENSE.txt` and `licenses/ZVec-NOTICE.txt`

The native runtimes include `bindings/native/zvec_rabitq_extension.cc` from OpenIndexer.ZVec in the ZVec C API target. This extension adds versioned C exports for constructing, inspecting, attaching, and destroying the upstream C++ `HnswRabitqIndexParams` and `HnswRabitqQueryParams` types. It does not replace or relabel the stock ZVec index factory.

## RaBitQ-Library

- Project source included by ZVec under `thirdparty/RaBitQ-Library`
- License: Apache License 2.0
- Included file: `licenses/RaBitQ-LICENSE.txt`

The packaged Linux x64 runtime requires an x86-64 CPU. HNSW-RaBitQ additionally requires AVX2 or newer instructions. Other ZVec index types do not imply HNSW-RaBitQ support on unsupported platforms.
