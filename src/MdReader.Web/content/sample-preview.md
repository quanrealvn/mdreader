## Release notes, 2.3.0

Two fixes and one thing worth reading before you upgrade.

> [!WARNING]
> The `--strict` flag now fails on warnings it used to print. Run a build before you
> deploy.

### Fixed

- [x] Timestamps no longer shift by an hour across a DST boundary
- [x] `parse()` keeps the original line endings
- [ ] The Windows installer still needs a signature

| Component | Before | After |
| --------- | -----: | ----: |
| Parser    | 180 ms | 41 ms |
| Renderer  |  95 ms | 92 ms |

```ts
const result = parse(source, { strict: true });
if (!result.ok) throw new Error(result.reason);
```

Reported by a reader who noticed the hour was wrong.[^1]

[^1]: Issue 412, reported on a Sunday in October, which is how these things go.
