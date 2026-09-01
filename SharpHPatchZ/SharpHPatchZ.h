/*
 * SharpHPatchZ native API
 *
 * This header describes the exports in HPatch.UnmanagedExtern.cs. The exports
 * are available when SharpHPatchZ is published as a .NET 8+ NativeAOT shared
 * library.
 *
 * SPDX-License-Identifier: MIT
 */

#ifndef SHARP_HPATCH_Z_H
#define SHARP_HPATCH_Z_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

/*
 * Define SHPZ_STATIC when no import/export decoration is required.
 * Define SHPZ_BUILD only while building the shared library itself.
 */
#ifndef SHPZ_API
#  if defined(_WIN32) && !defined(SHPZ_STATIC)
#    if defined(SHPZ_BUILD)
#      define SHPZ_API __declspec(dllexport)
#    else
#      define SHPZ_API __declspec(dllimport)
#    endif
#  elif defined(__GNUC__) && defined(SHPZ_BUILD)
#    define SHPZ_API __attribute__((visibility("default")))
#  else
#    define SHPZ_API
#  endif
#endif

/*
 * HPatch.UnmanagedExtern.cs uses cdecl unless its USEWINDOWS compilation
 * symbol is defined. Define SHPZ_USE_STDCALL when consuming such a build.
 */
#ifndef SHPZ_CALL
#  if defined(_WIN32) && defined(SHPZ_USE_STDCALL)
#    define SHPZ_CALL __stdcall
#  elif defined(_MSC_VER)
#    define SHPZ_CALL __cdecl
#  elif defined(__i386__) && (defined(__GNUC__) || defined(__clang__))
#    define SHPZ_CALL __attribute__((cdecl))
#  else
#    define SHPZ_CALL
#  endif
#endif

/* Progress callbacks are always cdecl, including in stdcall API builds. */
#ifndef SHPZ_CALLBACK
#  if defined(_MSC_VER)
#    define SHPZ_CALLBACK __cdecl
#  elif defined(__i386__) && (defined(__GNUC__) || defined(__clang__))
#    define SHPZ_CALLBACK __attribute__((cdecl))
#  else
#    define SHPZ_CALLBACK
#  endif
#endif

#if defined(__cplusplus)
extern "C" {
#endif

/* .NET char is always a 16-bit UTF-16 code unit; wchar_t is not portable. */
typedef uint16_t shpz_char16_t;

/* Fixed-width representations of the C# enums used by the ABI. */
typedef int32_t shpz_hdiff_magic;
enum {
    SHPZ_HDIFF_MAGIC_UNKNOWN = 0,
    SHPZ_HDIFF_MAGIC_HDIFF13 = 1,
    SHPZ_HDIFF_MAGIC_HDIFF19 = 2
};

typedef int32_t shpz_hdiff_compression;
enum {
    SHPZ_HDIFF_COMPRESSION_UNCOMPRESSED = 0,
    SHPZ_HDIFF_COMPRESSION_LZMA         = 1,
    SHPZ_HDIFF_COMPRESSION_LZMA2        = 2,
    SHPZ_HDIFF_COMPRESSION_ZLIB         = 3,
    SHPZ_HDIFF_COMPRESSION_PBZ2         = 4,
    SHPZ_HDIFF_COMPRESSION_BZ2          = 5,
    SHPZ_HDIFF_COMPRESSION_ZSTD         = 6
};

typedef int32_t shpz_hdiff_checksum;
enum {
    SHPZ_HDIFF_CHECKSUM_NONE    = 0,
    SHPZ_HDIFF_CHECKSUM_FADLER64 = 1,
    SHPZ_HDIFF_CHECKSUM_CRC32   = 2
};

typedef int16_t shpz_metadata_type;
#define SHPZ_METADATA_MARKER          ((shpz_metadata_type)(uint16_t)0x8080u)
#define SHPZ_METADATA_PATCH           ((shpz_metadata_type)(uint16_t)0xC080u)
#define SHPZ_METADATA_DIRECTORY_PATCH ((shpz_metadata_type)(uint16_t)0xA080u)
#define SHPZ_METADATA_CHECKSUM_DATA   ((shpz_metadata_type)(uint16_t)0x9080u)
#define SHPZ_METADATA_ARRAY           ((shpz_metadata_type)(uint16_t)0x8880u)
#define SHPZ_METADATA_UTF16_STRING    ((shpz_metadata_type)(uint16_t)0x8480u)

typedef int32_t shpz_last_error_message_type;
enum {
    SHPZ_LAST_ERROR_MESSAGE           = 1,
    SHPZ_LAST_ERROR_STACK_TRACE       = 2,
    SHPZ_LAST_ERROR_MESSAGE_AND_TRACE = 3
};

/* Values returned by the exported functions. */
enum {
    SHPZ_SUCCESS = 0,
    SHPZ_ERROR_UNKNOWN = -1,

    SHPZ_ERROR_HEADER_MAGIC_NOT_SUPPORTED       = 0x10,
    SHPZ_ERROR_COMPRESSION_NOT_SUPPORTED        = 0x11,
    SHPZ_ERROR_CHECKSUM_NOT_SUPPORTED           = 0x12,
    SHPZ_ERROR_PATCH_FACTORY_NOT_SUPPORTED      = 0x13,

    SHPZ_ERROR_INFO_NOT_ALLOCATED               = 0x30,
    SHPZ_ERROR_DIRECTORY_METADATA_NOT_ALLOCATED = 0x31,
    SHPZ_ERROR_PATCH_METADATA_NOT_ALLOCATED     = 0x32,
    SHPZ_ERROR_FILE_DESCRIPTOR_NULL             = 0x33,
    SHPZ_ERROR_ARGUMENT_NULL                    = 0x34,

    SHPZ_ERROR_HEADER_SIGNATURE_UNREADABLE      = 0x50,
    SHPZ_ERROR_END_OF_FILE_OR_DATA              = 0x51,
    SHPZ_ERROR_PATH_INVALID                     = 0x52,
    SHPZ_ERROR_IO                               = 0x53,
    SHPZ_ERROR_INPUT_PATH_NOT_FOUND             = 0x54,
    SHPZ_ERROR_INPUT_SIZE_MISMATCH              = 0x55,
    SHPZ_ERROR_PATH_NOT_DIRECTORY               = 0x56,
    SHPZ_ERROR_PATH_NOT_FILE                    = 0x57,
    SHPZ_ERROR_INPUT_FILES_MISMATCH             = 0x58,
    SHPZ_ERROR_KURO_INPUT_SIZE_MISMATCH         = 0x59,
    SHPZ_ERROR_STREAM_READ_OUT_OF_BOUNDS        = 0x5A,
    SHPZ_ERROR_STRING_ENCODING                  = 0x5B,

    SHPZ_ERROR_LZMA_PROPERTY_MISSING            = 0xA0,
    SHPZ_ERROR_LZMA2_DICTIONARY_INVALID         = 0xA1,
    SHPZ_ERROR_LZMA2_NO_COMPRESSED_PAYLOAD      = 0xA2,
    SHPZ_ERROR_LZMA_DICTIONARY_LENGTH_INVALID   = 0xA3,
    SHPZ_ERROR_LZMA_DATA_TOO_SMALL              = 0xA4
};

/* Match StructLayout(LayoutKind.Sequential), whose default packing is 8. */
#if defined(_MSC_VER) || defined(__GNUC__) || defined(__clang__)
#  pragma pack(push, 8)
#endif

typedef struct shpz_initialize_options {
    int32_t is_kuro_games_hdiff;
} shpz_initialize_options;

typedef struct shpz_patch_options {
    uint8_t  use_simd;
    uint8_t  _padding0[3];
    uint32_t parallel_threads;
    int32_t  reader_buffer_size;
    int32_t  copy_buffer_size;
    int32_t  patch_worker_buffer_size;
} shpz_patch_options;

typedef void (SHPZ_CALLBACK *shpz_processed_bytes_callback)(
    int64_t total_processed,
    int64_t total_size,
    int32_t written);

typedef struct shpz_progress_callback {
    shpz_processed_bytes_callback processed_bytes;
} shpz_progress_callback;

typedef struct shpz_hdiff_info {
    shpz_hdiff_magic       magic_type;
    shpz_hdiff_compression compression_type;
    shpz_hdiff_checksum    checksum_type;
    shpz_initialize_options initialize_options;
    void                   *metadata;
} shpz_hdiff_info;

typedef struct shpz_chunk_size_info {
    int64_t size;
    int64_t compressed_size;
} shpz_chunk_size_info;

typedef struct shpz_entry_count_size_info {
    int32_t count;
    int64_t size;
} shpz_entry_count_size_info;

typedef struct shpz_file_index_pair {
    int32_t old_index;
    int32_t new_index;
} shpz_file_index_pair;

typedef struct shpz_extern_size_info {
    int32_t new_execute_count;
    int64_t private_reserved_data_size;
    int64_t private_extern_data_size;
    int64_t extern_data_size;
} shpz_extern_size_info;

typedef struct shpz_native_string_utf16 {
    shpz_char16_t *chars;
    int32_t        length;
} shpz_native_string_utf16;

typedef struct shpz_utf16_string {
    shpz_metadata_type      metadata_type;
    uint8_t                 is_initialized;
    uint8_t                 is_disposed;
    shpz_native_string_utf16 native;
} shpz_utf16_string;

/* These three structs mirror different closed forms of UnmanagedArray<T>. */
typedef struct shpz_utf16_string_array {
    shpz_metadata_type metadata_type;
    uint8_t            is_initialized;
    uint8_t            is_disposed;
    int32_t            length;
    int32_t            element_size;
    shpz_utf16_string *data;
} shpz_utf16_string_array;

typedef struct shpz_int32_array {
    shpz_metadata_type metadata_type;
    uint8_t            is_initialized;
    uint8_t            is_disposed;
    int32_t            length;
    int32_t            element_size;
    int32_t           *data;
} shpz_int32_array;

typedef struct shpz_int64_array {
    shpz_metadata_type metadata_type;
    uint8_t            is_initialized;
    uint8_t            is_disposed;
    int32_t            length;
    int32_t            element_size;
    int64_t           *data;
} shpz_int64_array;

typedef struct shpz_checksum_data_info {
    shpz_metadata_type metadata_type;
    uint8_t            is_initialized;
    uint8_t            is_disposed;
    int32_t            element_size;
    int32_t            element_count;
    uint8_t           *data;
} shpz_checksum_data_info;

typedef struct shpz_patch_metadata {
    shpz_metadata_type metadata_type;
    uint8_t            is_initialized;
    uint8_t            is_disposed;
    int64_t            diff_new_size;
    int64_t            diff_old_size;
    int64_t            diff_data_offset;
    int32_t            cover_data_count;
    shpz_chunk_size_info *cover_data_size;
    shpz_chunk_size_info *rle_control_data_size;
    shpz_chunk_size_info *rle_code_data_size;
    shpz_chunk_size_info *new_diff_data_size;
} shpz_patch_metadata;

typedef struct shpz_directory_patch_metadata {
    shpz_metadata_type metadata_type;
    uint8_t            is_initialized;
    uint8_t            is_disposed;
    uint8_t            is_input_directory;
    uint8_t            is_output_directory;

    shpz_entry_count_size_info *input_path_count_size;
    shpz_entry_count_size_info *output_path_count_size;
    shpz_entry_count_size_info *same_file_path_count_size;
    shpz_file_index_pair       *same_file_path_index_pairs;

    shpz_utf16_string_array *input_paths;
    shpz_utf16_string_array *output_paths;
    shpz_int32_array        *input_file_indices;
    shpz_int64_array        *input_file_sizes;
    shpz_int32_array        *output_file_indices;
    shpz_int64_array        *output_file_sizes;
    shpz_int64_array        *output_file_hashes;

    shpz_extern_size_info   *extern_size;
    shpz_chunk_size_info    *head_data_size;
    shpz_patch_metadata     *patch_metadata;
    shpz_checksum_data_info *checksum_data;
    shpz_int32_array        *new_execute_indices;
} shpz_directory_patch_metadata;

#if defined(_MSC_VER) || defined(__GNUC__) || defined(__clang__)
#  pragma pack(pop)
#endif

/* Convenient initializers matching the unmanaged entry points' defaults. */
static inline shpz_initialize_options shpz_make_initialize_options(void)
{
    shpz_initialize_options value = { 0 };
    return value;
}

static inline shpz_patch_options shpz_make_patch_options(void)
{
    shpz_patch_options value = { 0 };
    value.use_simd = 1;
    return value;
}

static inline shpz_progress_callback shpz_make_progress_callback(
    shpz_processed_bytes_callback callback)
{
    shpz_progress_callback value;
    value.processed_bytes = callback;
    return value;
}

/*
 * String arguments documented as "auto string" accept a null-terminated UTF-8
 * string or a null-terminated UTF-16LE string. UTF-16 must use shpz_char16_t,
 * not wchar_t on platforms where wchar_t is 32 bits.
 */

SHPZ_API int32_t SHPZ_CALL shpz_read_header_signature_string(
    const void              *signature,
    shpz_hdiff_magic        *magic_type,
    shpz_hdiff_compression  *compression_type,
    shpz_hdiff_checksum     *checksum_type);

/*
 * On success, info owns metadata allocated by SharpHPatchZ. Release it exactly
 * once with shpz_free_diff_info. The memory buffer only needs to remain valid
 * for the duration of this call.
 */
SHPZ_API int32_t SHPZ_CALL shpz_init_from_memory(
    const uint8_t                 *data,
    int64_t                        data_length,
    shpz_hdiff_info               *info,
    const shpz_initialize_options *options);

SHPZ_API int32_t SHPZ_CALL shpz_init_from_filepath(
    const void                    *patch_path,
    shpz_hdiff_info               *info,
    const shpz_initialize_options *options);

/* The FILE remains owned by the caller and is not closed by SharpHPatchZ. */
SHPZ_API int32_t SHPZ_CALL shpz_init_from_FILE(
    FILE                          *patch_file,
    shpz_hdiff_info               *info,
    const shpz_initialize_options *options);

SHPZ_API int32_t SHPZ_CALL shpz_patch_from_filepath(
    const void                   *patch_path,
    const void                   *input_path,
    const void                   *output_path,
    const shpz_hdiff_info        *info,
    const shpz_patch_options     *options,
    const shpz_progress_callback *progress);

/* The FILE remains owned by the caller and is not closed by SharpHPatchZ. */
SHPZ_API int32_t SHPZ_CALL shpz_patch_from_FILE(
    FILE                         *patch_file,
    const void                   *input_path,
    const void                   *output_path,
    const shpz_hdiff_info        *info,
    const shpz_patch_options     *options,
    const shpz_progress_callback *progress);

SHPZ_API int32_t SHPZ_CALL shpz_free_diff_info(shpz_hdiff_info *info);

/* Returned pointers are borrowed and become invalid after free_diff_info. */
SHPZ_API const shpz_patch_metadata *SHPZ_CALL
shpz_util_get_patch_metadata(const shpz_hdiff_info *info);

SHPZ_API const shpz_directory_patch_metadata *SHPZ_CALL
shpz_util_get_directory_patch_metadata(const shpz_hdiff_info *info);

/*
 * buffer_length is measured in bytes for A and in UTF-16 code units for W.
 * The return value excludes the terminating NUL. A return value of -1 means
 * that the supplied buffer was too small or conversion failed. Last-error
 * state is shared by the process rather than stored per thread.
 */
SHPZ_API int32_t SHPZ_CALL shpz_get_last_errorA(
    uint8_t                      *buffer,
    int32_t                       buffer_length,
    shpz_last_error_message_type  message_type);

SHPZ_API int32_t SHPZ_CALL shpz_get_last_errorW(
    shpz_char16_t                *buffer,
    int32_t                       buffer_length,
    shpz_last_error_message_type  message_type);

#if defined(__cplusplus)
} /* extern "C" */
#endif

/* Fail compilation early if consumer options changed an ABI-sensitive layout. */
#if defined(__cplusplus) && __cplusplus >= 201103L
#  define SHPZ_STATIC_ASSERT(condition, message) static_assert(condition, message)
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
#  define SHPZ_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

#if defined(SHPZ_STATIC_ASSERT)
SHPZ_STATIC_ASSERT(sizeof(shpz_hdiff_magic) == 4, "shpz_hdiff_magic must be 4 bytes");
SHPZ_STATIC_ASSERT(sizeof(shpz_metadata_type) == 2, "shpz_metadata_type must be 2 bytes");
SHPZ_STATIC_ASSERT(sizeof(shpz_initialize_options) == 4, "shpz_initialize_options layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_patch_options) == 20, "shpz_patch_options layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_progress_callback) == sizeof(void *), "shpz_progress_callback layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_hdiff_info) == (sizeof(void *) == 8 ? 24 : 20),
                   "shpz_hdiff_info size mismatch");
SHPZ_STATIC_ASSERT(offsetof(shpz_hdiff_info, metadata) == 16, "shpz_hdiff_info layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_chunk_size_info) == 16, "shpz_chunk_size_info layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_entry_count_size_info) == 16, "shpz_entry_count_size_info layout mismatch");
SHPZ_STATIC_ASSERT(offsetof(shpz_entry_count_size_info, size) == 8, "8-byte field alignment mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_file_index_pair) == 8, "shpz_file_index_pair layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_extern_size_info) == 32, "shpz_extern_size_info layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_native_string_utf16) == sizeof(void *) * 2,
                   "shpz_native_string_utf16 layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_utf16_string) == (sizeof(void *) == 8 ? 24 : 12),
                   "shpz_utf16_string layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_utf16_string_array) == (sizeof(void *) == 8 ? 24 : 16),
                   "shpz_utf16_string_array layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_int32_array) == (sizeof(void *) == 8 ? 24 : 16),
                   "shpz_int32_array layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_int64_array) == (sizeof(void *) == 8 ? 24 : 16),
                   "shpz_int64_array layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_checksum_data_info) == (sizeof(void *) == 8 ? 24 : 16),
                   "shpz_checksum_data_info layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_patch_metadata) == (sizeof(void *) == 8 ? 72 : 56),
                   "shpz_patch_metadata size mismatch");
SHPZ_STATIC_ASSERT(offsetof(shpz_patch_metadata, diff_new_size) == 8, "shpz_patch_metadata layout mismatch");
SHPZ_STATIC_ASSERT(sizeof(shpz_directory_patch_metadata) == (sizeof(void *) == 8 ? 136 : 72),
                   "shpz_directory_patch_metadata size mismatch");
SHPZ_STATIC_ASSERT(offsetof(shpz_directory_patch_metadata, input_path_count_size) == 8,
                   "shpz_directory_patch_metadata layout mismatch");
#  undef SHPZ_STATIC_ASSERT
#endif

#endif /* SHARP_HPATCH_Z_H */
