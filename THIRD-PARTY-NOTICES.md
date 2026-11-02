# Third-Party Notices

This repository's own code is licensed under the MIT License in [`LICENSE`](LICENSE). It also
carries content derived from third-party projects, reproduced here with each project's notice as
its license requires.

## opencode

Upstream project: [anomalyco/opencode](https://github.com/anomalyco/opencode)

Derived content in this repository:

- `spec/openapi.json` — the accepted snapshot of upstream's `packages/protocol/openapi.json`, at
  the exact commit [`spec/SNAPSHOT.md`](spec/SNAPSHOT.md) pins. It is produced from upstream's own
  generator, so it is a derived copy rather than an independent description.
- `spec/patches/` — snapshot-production patches whose context lines quote upstream TypeScript
  source. The patches themselves are this repository's work; the quoted context is upstream's.
- `external/opencode` — a submodule reference. Nothing of it is redistributed by this repository;
  a clone fetches it from upstream directly.

Generated SDK source under `src/OpenCode.Sdk/` is emitted by this repository's own generator from
the accepted snapshot. Upstream's notice follows.

```
MIT License

Copyright (c) 2025 opencode

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Polyfill

Upstream project: [SimonCropp/Polyfill](https://github.com/SimonCropp/Polyfill)

Derived content: Polyfill is a source-only package, so its source is compiled into this
repository's packages. Its notice follows.

```
MIT License

Copyright (c) Simon Cropp

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
