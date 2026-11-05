# shellcheck shell=bash
# discover-llama-server.sh — find the llama-server binary a live runner should use under a node data root.

# Prints the path and returns 0, or returns 1 when the data root holds no llama-server.
discover_server() {
  local data_root="$1" candidate
  # The source build is preferred: on a CUDA box it is the binary that actually runs the GPU, and
  # its layout is fixed (LlamaCppSourceBuildService: {root}/llama.cpp/source-build/active/build/bin).
  candidate="${data_root}/llama.cpp/source-build/active/build/bin/llama-server"
  if [[ -x "${candidate}" ]]; then
    printf '%s\n' "${candidate}"
    return 0
  fi
  # Otherwise any acquired variant under {root}/llama.cpp (cuda / vulkan / cpu), newest first.
  if [[ -d "${data_root}/llama.cpp" ]]; then
    candidate="$(find "${data_root}/llama.cpp" -type f -name llama-server -perm -u+x -printf '%T@\t%p\n' 2>/dev/null \
                 | sort -rn | head -1 | cut -f2-)"
    if [[ -n "${candidate}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  fi
  return 1
}
