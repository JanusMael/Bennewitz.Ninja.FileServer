# Third-party notices

Bennewitz.Ninja.FileServer redistributes the third-party material listed below. Each component's
licence is reproduced in full.

Packages referenced from NuGet — `Markdig` and `ColorCode.HTML` — are deliberately absent from
this file. They are dependencies rather than copies: NuGet delivers them as their own packages,
carrying their own licences, and nothing of theirs is reproduced here.

---

## github-markdown-css 5.9.0

Vendored as `src/Bennewitz.Ninja.FileServer/wwwroot/css/github-markdown.min.css` (the jsDelivr
minification of the original) and embedded in the `Bennewitz.Ninja.FileServer.Hosting` assembly,
where it styles rendered Markdown and supplies the token palette used for syntax highlighting.

Colour values from its light and dark palettes are additionally reproduced in
`src/Bennewitz.Ninja.FileServer/wwwroot/css/fileserver.css`, in the blocks that pin a colour
scheme when a reader overrides the system preference. A media query cannot be disabled for a
single element, so those values have to be restated rather than referenced.

- Project: <https://github.com/sindresorhus/github-markdown-css>
- Author: Sindre Sorhus
- Licence: MIT

```text
MIT License

Copyright (c) Sindre Sorhus <sindresorhus@gmail.com> (https://sindresorhus.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
