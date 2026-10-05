# shellcheck shell=bash
# git-env.sh — the ONE list of environment variables that make git pick a repository, work tree, index or
# object store other than the one its working directory (or -C) points at. `git -C <dir>` does not override
# them: an exported GIT_DIR answers every query for that other repository, and this repository's own working
# rules have agents export a private GIT_INDEX_FILE. The gates and checks (run-backend-tests.sh,
# run-release-contract-tests.sh, openapi-live-check.sh, assembly-guard.sh, lint-release-scripts.sh) drop them at
# the top so neither they nor any child can be steered elsewhere. scripts/lib/build-lock-common.sh strips them
# per git call instead (xe_git): the lock wrapper runs arbitrary commands, which must keep seeing what their
# caller exported, and a library cannot rely on its sourcer having dropped them.

# Only variables that SELECT something, each named for what it selects:
#   GIT_DIR                           the repository
#   GIT_WORK_TREE                     the work tree
#   GIT_INDEX_FILE                    the index
#   GIT_COMMON_DIR                    the shared repository behind a linked worktree (refs, objects, config)
#   GIT_OBJECT_DIRECTORY              the object store
#   GIT_ALTERNATE_OBJECT_DIRECTORIES  extra object stores objects are read from
#   GIT_NAMESPACE                     the ref namespace, i.e. which HEAD and branches are seen
# Deliberately NOT listed: GIT_CEILING_DIRECTORIES and GIT_DISCOVERY_ACROSS_FILESYSTEM only BOUND how far
# discovery walks. Dropping them would let a copy without its own .git (a fixture under .tmp/) discover the
# enclosing repository and write its lock there, so a caller's boundary is respected. Config variables
# (GIT_CONFIG*) configure, they do not select, and are left alone too.
XE_GIT_REPO_ENV_VARS=(
  GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_COMMON_DIR GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES
  GIT_NAMESPACE
)

# Removes them from this shell and everything it starts from now on.
xe_drop_git_repo_env() { unset "${XE_GIT_REPO_ENV_VARS[@]}"; }

# Runs git with them removed, for code that must not change its caller's environment.
xe_git() {
  local -a unset_args=()
  local var
  for var in "${XE_GIT_REPO_ENV_VARS[@]}"; do unset_args+=(-u "${var}"); done
  env "${unset_args[@]}" git "$@"
}
