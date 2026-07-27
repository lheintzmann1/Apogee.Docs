# Lua editor setup

The Lua API reference is generated from the sol2 bindings, and the same extraction emits a
[LuaCATS definition file](../../media/apogee.d.lua) describing the whole `Apogee` table. Pointing
your editor at it gives completion, signature help and hover documentation for the engine API,
using exactly the same data as the website.

The file is regenerated on every docs build, so it never drifts from the engine revision it was
built against.

## Any editor with lua-language-server

[lua-language-server](https://github.com/LuaLS/lua-language-server) backs the Lua extension in
VS Code, Neovim, Zed and others. Download `apogee.d.lua` into your project (`.luarc/` is a
reasonable home) and add it to the workspace library:

```json
{
  "$schema": "https://raw.githubusercontent.com/LuaLS/vscode-lua/master/setting/schema.json",
  "runtime.version": "Lua 5.4",
  "workspace.library": [".luarc"],
  "diagnostics.globals": ["Apogee"]
}
```

Save that as `.luarc.json` at the root of your project. `diagnostics.globals` stops the server
from flagging `Apogee` as an undefined global before it has loaded the definitions.

## VS Code

Install the **Lua** extension (sumneko), then use the `.luarc.json` above — the extension reads it
automatically. No further settings are needed.

## Neovim

With `nvim-lspconfig`:

```lua
require('lspconfig').lua_ls.setup {
  settings = {
    Lua = {
      runtime = { version = 'Lua 5.4' },
      workspace = { library = { vim.fn.getcwd() .. '/.luarc' } },
      diagnostics = { globals = { 'Apogee' } },
    },
  },
}
```

## Keeping it current

`apogee.d.lua` is a build artifact of the docs repository. To refresh it against a newer engine:

```bash
cd Apogee.Docs
./build.sh cpp     # gives the Lua step its type information
./build.sh lua     # writes media/apogee.d.lua
```

Running `lua` without `cpp` still produces a complete file, but bindings that forward to a C++
member will lack their parameter names and types.
