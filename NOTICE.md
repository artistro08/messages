# Notice

This repository contains a C# port of `pkg/libgm` and its `.proto` files from [mautrix-gmessages](https://github.com/mautrix/gmessages), Copyright (c) Tulir Asokan, licensed under the AGPL-3.0.

The port follows the upstream source at commit [`ba33f4875a491f5cb47db8a6baf42a8869f5f383`](https://github.com/mautrix/gmessages/tree/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm).

This repository is licensed under the AGPL-3.0 as well. See `LICENSE`.

## Third-Party Assets

The Apache License 2.0 text ships with the app as `Assets/Emoji/LICENSE-Apache-2.0.txt` and covers both items below.

- **Noto Color Emoji images** (`src/GMessages.App/Assets/Emoji`): the pairing emoji, from [googlefonts/noto-emoji](https://github.com/googlefonts/noto-emoji) (`2D/png/128`, commit `99701e07d3d1fe1bdbcdb14964547f0fd592966f`), Copyright 2013 Google LLC, Apache License 2.0 (the repository's README: "Tools and most image resources are under the Apache license, version 2.0"). The repository's own `LICENSE` file (SIL Open Font License 1.1, for its fonts) is kept beside them as `LICENSE-OFL-1.1.txt`.
- **Material Symbols `mobile_off`** (the Unpair icon's path, in `MainWindow.xaml` and `SettingsPage.xaml`): from [google/material-design-icons](https://github.com/google/material-design-icons), Copyright Google LLC, Apache License 2.0.
