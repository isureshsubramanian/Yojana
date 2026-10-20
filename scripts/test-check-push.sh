#!/bin/sh
# Exercise the guard with commit paths supplied by a tiny git stand-in.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
mkdir -p "$root/artifacts"
test_dir=$(mktemp -d "$root/artifacts/push-guard.XXXXXX")
trap 'rm -rf "$test_dir"' EXIT HUP INT TERM
cat > "$test_dir/git" <<'GIT'
#!/bin/sh
printf '%s\n' "$GUARD_TEST_PATH"
GIT
chmod +x "$test_dir/git"
for path in docs docs/plan/internal.md nested/docs nested/docs/internal.md; do
  if printf 'refs/heads/test abc refs/heads/test def\n' | GUARD_TEST_PATH="$path" PATH="$test_dir:$PATH" sh "$root/scripts/check-push.sh" > /dev/null 2>&1; then
    echo "guard incorrectly accepted $path" >&2
    exit 1
  fi
done
printf 'refs/heads/test abc refs/heads/test def\n' | GUARD_TEST_PATH='src/feature.cs' PATH="$test_dir:$PATH" sh "$root/scripts/check-push.sh"
echo 'Push guard: 5 cases passed.'
