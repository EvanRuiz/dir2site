<!-- SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors -->
<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
# Adding videos

Drop a Windows internet shortcut (`.url`) pointing at a YouTube video into your project folder and
Dir2Site turns it into a **Video** artifact: the poster frame is downloaded for the card, and the
video plays **inline on the collection page**.

Videos are the one artifact type that gets no page of its own. A photo or a PDF has a `Portrait/`
page you click through to; a video is the card, and pressing play starts it where it sits.

---

## The basics

Create a file called, say, `My Talk.url` containing:

```ini
[InternetShortcut]
URL=https://www.youtube.com/watch?v=AbCdEfGhIjK
```

This is the format Windows writes when you drag a link to the desktop, and what most browsers and
file managers produce when you save a link. On macOS and Linux you can just write the two lines by
hand in any text editor.

On the next scan Dir2Site creates `My Talk.url.yaml` next to it:

```yaml
type: video
caption: My Talk      # the title shown on the card
credit:               # optional attribution line
provider: youtube
videoId: AbCdEfGhIjK
start:                # playback offset in seconds
date:
url:                  # blank sends the link to the .url shortcut's own address
url-text:             # fill in to add a link out to the video; blank means no link
home: false
parent-cover: false
grandparent-cover: false
```

The last few are the settings every artifact has; see [Artifact settings](../README.md#artifact-settings).

Cards carry no outbound link by default — the player already offers YouTube's own. Put text in
`url-text` (e.g. `View on YouTube`) and the card gains a link to the URL from your `.url` file.

This is the one place `url-text` works without a `url` beside it: a video's address comes from the
shortcut, so there is nothing for you to type. Fill in `url` anyway and the link goes there instead
— the talk's own page rather than the upload, say — and it needs no `url-text`, taking the address
as its own words like every other artifact does. Because a video has no page of its own, the link
sits on the card, where every other artifact's sits under the credit line on its page.

Edit the caption and credit in the app or directly in the YAML, exactly as for any other artifact.

---

## Link formats that work

Any of these are recognised, with or without `www.` or `m.`:

| Form | Example |
|---|---|
| Watch page | `https://www.youtube.com/watch?v=AbCdEfGhIjK` |
| Short link | `https://youtu.be/AbCdEfGhIjK` |
| Embed | `https://www.youtube.com/embed/AbCdEfGhIjK` |
| Short | `https://www.youtube.com/shorts/AbCdEfGhIjK` |
| Live | `https://www.youtube.com/live/AbCdEfGhIjK` |

Extra parameters are ignored, so a link copied out of a playlist — carrying `&list=` and `&index=` —
works as-is. YouTube is currently the only supported provider.

**A `.url` pointing anywhere else is not an artifact.** An ordinary web bookmark filed among your
photos is skipped silently: no yaml is created and nothing appears on the page.

---

## Starting partway through

If the saved link has a time offset, it is picked up automatically. All the forms YouTube writes are
understood — `t=90`, `t=90s`, `t=1m30s`, and `start=90`:

```ini
[InternetShortcut]
URL=https://www.youtube.com/watch?v=AbCdEfGhIjK&t=1m30s
```

gives you `start: 90` in the yaml. You can also set or change `start:` by hand — a value you have
put there yourself is never overwritten by the link.

---

## The shortcut is the source of truth

`provider` and `videoId` are re-read from the `.url` on every generate. Re-point the shortcut at a
different video and regenerate: the card moves to the new video, the poster is re-downloaded, and
the yaml is brought back into line. Editing `videoId` in the yaml by hand does not stick,
because the `.url` will overwrite it on the next run — change the link instead.

`caption`, `credit`, `url-text`, `start` and the settings every artifact has are yours; nothing
overwrites those.

---

## Thumbnails

The poster frame is downloaded from YouTube at generate time and stored alongside your other
previews in `.dir2site/`, at 16:9 rather than the 4:3 used for photos. Two consequences worth
knowing:

- **Generating a site with videos needs a network connection**, once per video. After that the
  poster is cached and reused until the `.url` changes. If the download fails, the card falls back
  to a plain placeholder and generation carries on.
- **The poster is what the card shows while the page is loading**, and what a visitor with
  JavaScript disabled keeps. Once the player is ready it takes over the card.

A video's poster is also what stands in as the thumbnail for the folder containing it, and as the
page's OpenGraph image, the same as any other artifact.

---

## Playback

Every video on the page gets a real YouTube player as soon as the page loads, so the play button you
see is YouTube's own and clicking it starts the video as directly as it possibly can. This is
deliberate: earlier versions kept our poster on top and started playback from script, and both the
delay and the missing feedback made a click feel like it hadn't registered.

The trade is that a page with several videos loads several players, so the videos are the expensive
part of a page rather than free until clicked.

When a video finishes, the poster comes back over the player — which covers YouTube's related-video
end screen, so a visitor is never handed off into someone else's content. Clicking the poster
replays from your `start` offset. Starting a second video pauses the first, so a page of videos
never ends up with two soundtracks at once.
