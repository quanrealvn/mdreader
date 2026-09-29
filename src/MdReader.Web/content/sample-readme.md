# widgetise

Turns a folder of SVG files into one sprite sheet and a typed index.

## Install

```bash
npm install --save-dev widgetise
```

## Use it

```bash
npx widgetise ./icons --out ./src/sprite
```

> [!NOTE]
> Files are matched by extension, not by content. A `.svg` that is not SVG will fail with
> the file name in the message.

<details>
<summary>Options</summary>

| Flag       | Default   | What it does                     |
| ---------- | --------- | -------------------------------- |
| `--out`    | `./dist`  | Where the sprite is written      |
| `--prefix` | `icon-`   | Prepended to every symbol id     |

</details>

## License

MIT
