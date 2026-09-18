# Klippy

A fast, cross-platform snippet manager. Store pieces of text, find them instantly, and
put them on the clipboard with one click, tap, or keystroke. Built with .NET 10 and
[Avalonia UI](https://avaloniaui.net/) for Windows, macOS, Android, and iOS.

![Desktop](docs/screenshot-desktop.png)

## Finding snippets

Two ways, both designed for speed:

- **Quick-codes** — a snippet can carry a short code (e.g. `slf` for "Send log files").
  Type the code in the search box and it jumps straight to the top; press Enter to copy.
- **Search** — every space-separated term must prefix-match a word anywhere in the
  label, content, or tag. `log fil` finds "…send us the **log** **fil**es…".
  Matching is case-insensitive; label hits rank above content hits, and recently used
  snippets rank first among ties.

The index is precomputed, lowercased words per snippet
([`SnippetSearch`](Klippy/Services/SnippetSearch.cs)), so a keystroke costs a linear
scan of ordinal `StartsWith` checks — microseconds for thousands of snippets.

**Keyboard (desktop):** type to filter, `↑`/`↓` to navigate, `Enter` to copy — or to
[run](#running-things-links-scripts-applications-and-macros) a snippet marked for it, or
[what you typed](#running-an-unmatched-search) when nothing matched at all —
`Ctrl/⌘+Enter` to copy one of those anyway, `F2` (or `Ctrl/⌘+I`) edit the selected snippet,
`Ctrl/⌘+N` new snippet, `Ctrl/⌘+D` duplicate the selected snippet, `Ctrl/⌘+F` focus
search, `Ctrl/⌘+P` toggle the preview pane, `Ctrl/⌘+E` export/import,
`Ctrl/⌘+,` settings, `↓` on an empty search box opens
[recent commands](#recent-commands-the-mru), `Esc`
clears/cancels, `Ctrl/⌘+Enter` saves in the editor. Clicking a row triggers it, the
same as `Enter`. Typing `quit` offers to [close Klippy](#quitting-desktop).

> `F2` is the edit key everywhere else, and `Ctrl/⌘+I` is there for Mac keyboards, where
> `F2` is the brightness key unless the function-key setting says otherwise. Both open
> the editor on the selected snippet; in the clipboard history, where a clip has no
> editor of its own, they do nothing.

A **preview pane** at the bottom shows the full content of the selected snippet —
useful for long or multi-line entries that the one-line row preview truncates. It is
closed by default and toggles with `Ctrl/⌘+P`, the `preview` footer link, or a click on
its own header. Drag the splitter above it to trade height with the list; the pane
reopens at whatever height you left it. Row-level expansion (the chevron on multi-line
rows) still works independently.

The **edit dialog** is resizable: drag the grip in its bottom-right corner to give the
CONTENT field more room, and it reopens at that size. Clicking the dimmed area beside
it does nothing on purpose — leaving an edit takes `Esc` or Cancel, so a stray click
cannot discard it.

A snippet carries a single **tag**, and the editor shows the tags already in use under
the TAG field (EXISTING TAGS) so a snippet joins one of them rather than quietly coining
`wrk` beside `work` — a typo there costs a chip in the filter row and hides the snippet
from the tag it belonged to. Click a tag to fill the field; click the highlighted one
again to clear it, which is how a tagged snippet goes back to untagged without the
keyboard. Typing still creates a new tag: a part-typed entry narrows the chips to the
tags it could still become, and an entry that is already a tag shows the whole set again
so the next click can move the snippet elsewhere. A long list is capped at three rows and
scrolls, with the snippet's own tag scrolled into view. The chips sit out of the tab
order — the TAG box is the keyboard route, and tabbing through a dozen of them to reach
QUICK-CODE would cost more than they give.

Snippets can be **duplicated** — from a row's hover actions or `Ctrl/⌘+D` on desktop,
or via the Duplicate button in the edit overlay (the route on mobile: swipe → Edit →
Duplicate). A duplicate opens prefilled as a new snippet with " (copy)" appended to the
label; the quick-code is deliberately not copied so codes stay unique.

## Running things: links, scripts, applications and macros

Some snippets are not text you want to paste — they are a link you want open, a script
you want run, or an application you want started. A snippet can be marked **Execute** in
the editor (WHEN TRIGGERED → Execute), exactly as it can be marked Markdown, and then
triggering it — `Enter`, a click, a tap — runs it instead of copying it. Without the
marker nothing runs: Klippy never decides on its own that a snippet looks like a link
and should therefore be launched.

Marked rows show a small amber `run` marker, the way Markdown ones show `md`, and their
primary hover action becomes **▷** rather than the copy glyph. `Ctrl/⌘+Enter` always
copies, marker or not, so a snippet you usually run can still be put on the clipboard
when you want the text — the row's badge says both: `↵ run · Ctrl+↵ copy`.

What a marked snippet can run is a short allow-list, checked against its first word,
once any environment variable in it has resolved:

| Snippet starts with | Runs as |
|---|---|
| `http://`, `https://`, `mailto:`, or a bare `www.` | Opened in the default browser |
| `*.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` on Windows, `pwsh -NoProfile -File` elsewhere |
| `*.sh` | The script itself where it is executable, so its `#!` line chooses; otherwise `/bin/sh`. `bash.exe` (Git Bash, WSL) on Windows |
| `*.bat`, `*.cmd` | `cmd.exe /c` — **Windows only** |
| `*.exe` | Started directly, arguments and all — **Windows only** |
| `*.app` | `open -a`, which knows which executable inside the bundle to start — **macOS only** |
| `*.AppImage` | The image itself, which runs itself — **Linux only** |
| anything else | Nothing — and the editor says so as you tick the marker, rather than leaving you to find out by pressing Enter |

The last three are what each platform calls an application, and each runs only at home:
an `.exe` is no more startable on a Mac than a `.bat` is, and marking one there warns
you in the editor rather than failing at the press of Enter.

Everything after the first word is passed to the script or application as arguments,
quotes grouping the words that belong together: `deploy.ps1 --env "west europe"` passes
two arguments, not three. That is also how a path with a space in it stays one path —

```
"C:\Program Files\Klippy\Klippy.Desktop.exe" --minimised
```

— and without the quotes the first word is `C:\Program`, which names nothing. A variable
whose value contains a space needs no quotes at all, because it resolves *after* the line
has been split into words: `%LOCALAPPDATA%\Programs\WebStorm\bin\webstorm64.exe` is one
path however many spaces your user name has in it. Scripts and applications alike run from
their own folder, which is where each normally expects to be, and a bare `notepad.exe` is
left to Windows to find on `PATH`, as Run would.

### Environment variables

The first word of a marked snippet may name itself the way a path does everywhere else on
the machine, in both dialects wherever you are: `%LOCALAPPDATA%` as on Windows, `$HOME` and
`${HOME}` as on macOS and Linux, plus a leading `~`. Windows will not do this for you —
only `cmd.exe` ever looked inside a file name, and nothing here goes through `cmd` — so
without it `%LOCALAPPDATA%\Programs\WebStorm\bin\webstorm64.exe` is a folder with percent
signs in its name and the launch fails on a path that plainly exists.

A name that does not resolve is left exactly as written, so it stays a string naming
nothing rather than quietly becoming a path with a hole in the middle of it. Two things it
deliberately does **not** touch:

- **Arguments.** Only the first word resolves. An argument keeps its percent signs, so the
  `.bat` refusal below still sees what `cmd.exe` would see, and a child process inherits
  the environment and can read its own `%APPDATA%` anyway.
- **A macro's value.** `%C%` holding a path with a `%VAR%` in it is left as it stands: a
  macro's value is data rather than more text to read, the same rule that keeps it from
  becoming a second command. It also means a clipboard holding `C:\100%discount%off\tool.exe`
  keeps its middle.

The same shorthands, read the same way, on the typed route — see
[Running an unmatched search](#running-an-unmatched-search). Two routes to the same
launcher should not disagree about what a variable means.

### Macros

A snippet's text can carry placeholders, filled in when it is copied or run — the
marker decides which of those happens, the macros work either way:

| Macro | Expands to |
|---|---|
| `%P%` | A positional argument — what you typed after the quick-code |
| `%C%` | Whatever text is on the clipboard right now |

Environment variables are not macros and the asymmetry is real: a `%VAR%` in the first
word resolves when an item **runs**, and a copy leaves it alone. A macro is the item's;
a variable belongs to the path the item names.

Say a snippet holds `https://www.google.com/search?q=%P%` behind the quick-code `?`.
Typing

```
? stuff
```

shows `https://www.google.com/search?q=stuff` on the row — the expansion, before you
have committed to anything — and `Enter` runs it if the snippet is marked, or copies
exactly that text if it is not. The *last* `%P%` takes every argument still unused, so
`? cats and dogs` searches for the phrase rather than throwing two thirds of it away. A
placeholder with nothing to fill it expands to nothing: half a typed invocation never
leaves `%P%` on the clipboard.

A line is only read as an invocation when its first word is **exactly** somebody's
quick-code and something follows it. Otherwise it is the ordinary search it has always
been — a prefix match would hijack every two-word search whose first word happened to
start with a quick-code.

Running hands the execution engine the snippet as stored *and* the arguments —
`https://www.google.com/search?q=%P%` and `stuff` — rather than the finished string,
because only it knows where each value is about to land: a URL's query string gets
`cats%20and%20dogs`, a script's argument list gets `cats and dogs`. `%C%` is read once,
at that moment, and only when the snippet actually carries one: a row previews its own
`%P%` expansion as you type, but Klippy never reads your clipboard to draw a list.

A `%C%` can carry the whole command — a snippet of just `%C%`, marked Execute, runs
whatever is on the clipboard, link or script or application path and switches alike,
which is the other half of the clipboard history.

### What running something will not do

Running a snippet is running code, so the edges are drawn deliberately tightly:

- **Nothing runs unmarked.** The marker is stored on the snippet and defaults to off, so
  every snippet that exists today — and every one an import brings in — goes on being
  copied.
- **Only the allow-list above runs**, applied to the first word once its variables have
  resolved. A snippet naming something that is neither link, script nor application is not
  runnable however firmly it is marked, so `docker system prune -af` stays text. A scheme
  is not a path whatever it ends in, either:
  `file:///C:/Windows/System32/cmd.exe` names an `.exe` without being one, and is
  refused along with `javascript:`.
- **An application is a program, and starting one is starting a program.** That is the
  point of the feature and also its edge: a snippet of `%C%` marked Execute will start
  whatever application path is on the clipboard, `cmd.exe` and its switches included. The
  marker is the gate — it is stored per snippet, defaults to off, and you put it there.
- **Arguments are passed as arguments**, never as a command line a shell re-reads. A
  `%C%` holding `; rm -rf ~` is one argument to the script, and stays one.
- **Except for `.bat`**, which `cmd.exe` re-parses after .NET has quoted it. An argument
  carrying `& | < > ^ " %` is refused with a message instead, because pretending to
  escape it would be a lie. The refusal is judged on the argument *as typed*, before
  anything resolves — a variable defined as `a&b` would otherwise smuggle an ampersand
  past the one check that exists to catch it — so `%APPDATA%` as an argument to a `.bat`
  is refused too. **The `.bat` file's own path faces the same rule**, minus the `%`: it
  rides the same line cmd.exe re-reads, and .NET quotes only what carries a space, so a
  folder genuinely called `R&D` would start a second command.
- **A path carrying a double quote is refused.** Windows stops the program name at the
  quote while the allow-list reads the extension off the end, so `payload.scr"x.exe`
  would pass as an `.exe` and start the `.scr`. A Windows path cannot contain a quote
  anyway — the same fact that lets a pasted *Copy as path* be unwrapped safely.
- `-File` rather than `-Command` for PowerShell, for the same reason: the arguments stay
  arguments instead of being parsed as more PowerShell. `-ExecutionPolicy Bypass` goes
  with it, since the script is one you keep in Klippy and have just asked for by name.

Running something dismisses the window, as copying can: the browser, the script or the
application is where you are going next. On a phone a marked link opens in the mobile
browser; a marked script or application says there is nothing to run it in rather than
doing nothing, and `%C%` and `%P%` expand on a copy there as they do everywhere.

### Pull first

Scripts tend to live in a checkout, and a checkout goes stale. With **Pull first** on,
running a script or an application runs `git pull` in its own folder and waits for it
before starting anything — so what runs is what is in the repository, not what was on
disk the last time you thought about it. It applies to a marked item and to an
[unmatched search](#running-an-unmatched-search) alike, since both end at the same
launcher.

It is a *separate process*, not a prefix: `git -C <folder> pull`, started the same way
everything else is, with its arguments as arguments. Klippy never builds a command line
for a shell to re-read — that is the property the whole execution path is built on, and
`git pull && …` would be the one place it was given up. It also means the pull can be
waited for and its exit code read, which a shell prefix could not offer.

- **Only a script or an application.** A link has no working copy, and a folder is being
  opened rather than run.
- **Only a real checkout.** The folder is walked up looking for `.git` — a directory in a
  clone, a file in a worktree or submodule — and the nearest one wins, so a script in a
  submodule pulls the submodule rather than its parent. Somewhere that is not a checkout
  is left alone, silently.
- **Only a rooted path.** A bare `notepad.exe` for Windows to find on `PATH` names no
  folder, and must not be read as one relative to wherever Klippy is running. A path named
  through a variable *is* rooted once it resolves, so a marked item behind `%LOCALAPPDATA%`
  is now eligible for a pull where it silently was not.
- **A failed pull does not cancel the run.** Off the network, on a conflicted branch, with
  no git installed: the script still starts and the toast says so —
  *Running deploy.ps1 — git pull failed (1)*. The pull is there to make what starts
  current, not to be a gate on starting at all. A pull that works says nothing, because
  it is what you asked for.
- **It waits, with a limit.** Thirty seconds, then the pull is killed and the run goes
  ahead with a note. `GIT_TERMINAL_PROMPT=0` goes with it, so a repository that wants a
  password fails in the moment instead of hanging on a prompt no one can see.
- The pull happens **before** the check that the target is there, so a script added in a
  commit this checkout has not seen yet is fetched rather than refused.

Off by default: it only makes sense where the things you run are kept in a checkout, it
costs a round trip to the remote on every run, and it is a network call made on your
behalf. In `settings.json` the key is `ExecutePullFirst`.

## Running an unmatched search

A search that matches nothing is usually a typo. Sometimes it is an instruction. When the
list comes up empty and what you typed names something runnable, Klippy offers to run it —
in a band where the first row would have been, carrying the same `↵` badge the rows do.
`Enter` runs it, and so does a click.

It is the feature above reached from the other end. A marked snippet is text you decided
in advance was runnable; this is a line you have just typed. Either way what comes out is
an [`ExecutionPlan`](Klippy/Services/ExecutionPolicy.cs) handed to the same
[`ProcessLauncher`](Klippy/Services/ProcessLauncher.cs), so there is one place in Klippy
that starts anything, and one toast that says how it went.

**An item that matches beats the offer — where the line could have been a search for it.**
A snippet called "Lock the server room door" keeps `lock` a filter for as long as it
exists: `lock` is an ordinary word, and that snippet is a plausible answer to it.

A rooted path or a link is not an ordinary word. Nobody types `D:\work\tools\` hoping to
filter a list, so a snippet whose body merely *mentions* that folder — a path to something
inside it, say, which matches every word of it — does not take the folder away from you.
There the offer stands beside the matches, and `Enter` runs it.

Being the line still beats mentioning it: a snippet whose text **is** the link you typed is
what you were looking for, and keeps both the selection and the keystroke. So does one
whose text is what the line resolved to, which is how `%APPDATA%` and the folder it expands
to stay the same request.

When the offer does stand beside a list with rows in it, it takes the selection and looks
the part — the same accent bar, accent label and `↵` badge a selected row carries — and the
list shows a badge on nothing, because only one of the two can have the keystroke. `↓` moves
into the list, where `Enter` activates the row exactly as it always did, and `↑` from the
first row comes back to the offer. It is a position in the same column of things, not a
banner above them.

In the clipboard history none of that applies and a matching clip always wins: clips are
mostly paths and links themselves, so a line that looks like one is far more likely to be
someone hunting for the clip they copied than an instruction.

| What you type | What happens |
|---|---|
| `%appdata%`, `$HOME`, `~/work`, `%appdata%\Klippy` | The variable expands and the folder opens |
| `C:\work\invoices`, `\\nas\share`, `/usr/local/bin` | The folder opens in Explorer / Finder |
| `C:\tools\deploy.ps1`, `D:\apps\thing.exe`, `/Applications/Safari.app` | The script or application runs, exactly as a marked snippet naming it would |
| `https://…`, `www.…` | The page opens |
| `lock`, `sleep`, `hibernate`, `restart` | The machine control, after a confirmation |

Two things are deliberately narrower here than for a snippet you marked yourself, because
a marked item was written on purpose and this is whatever landed in a filter box:

- **Links are `https:` and a bare `www.` only.** A marked snippet may also carry `http:`
  and `mailto:`; typed text may not. `http://` is left out because a launcher that
  silently sends you over plaintext is not doing you a favour, and the rest of the schemes
  were never on the list.
- **Paths must be rooted** — a drive, a UNC share, a leading `/`, a `~`, or a variable that
  expands to one. A relative path would resolve against wherever Klippy happened to be
  started from, which is nobody's mental model, and without the rule every unmatched word
  with a dot in it would look like a file.

Everything else is the allow-list you already know: a script or an application Klippy can
run on this platform, and nothing besides. A folder is the one addition — it is opened,
not executed — so a typed `C:\work\notes.txt` is still just text that matched nothing.

**Quotes come off.** Explorer's Shift+right-click → *Copy as path* wraps what it gives you
in double quotes, whether or not the path has a space in it, so a pasted path would
otherwise be a string starting with a quote and match nothing at all. A line wrapped in a
pair of them is unwrapped before anything else looks at it — both quotes or neither, since
an unmatched one is a half-finished paste and a Windows path cannot contain a quote
anyway. A snippet marked Execute has always tolerated them, because its line goes through
the argument splitter; this is the same courtesy on the typed route.

Environment variables read here exactly as they do in a marked snippet — see
[Environment variables](#environment-variables) — which is the point: `%APPDATA%` names one
folder whether you typed it into the filter box or wrote it into an item. A name that does not
resolve is left exactly as typed, so it stays a string that matches nothing rather than
quietly becoming a path with a hole in the middle of it. An item's own `%C%` and `%P%` are
left alone — those are filled when an item runs, and a search box is not an item.

The four machine controls are matched as the whole line and nothing else, so "restarting"
and "please restart" stay searches. Each asks before it happens, and that confirmation is
one `Enter` away so the whole gesture stays on the keyboard; it can be switched off. They
are planned as ordinary processes, which is why they need no second execution path:

| Control | Windows | macOS | Linux |
|---|---|---|---|
| **lock** | `rundll32 user32.dll,LockWorkStation` | `CGSession -suspend` | `loginctl lock-session` |
| **sleep** | `rundll32 powrprof.dll,SetSuspendState` | `pmset sleepnow` | `systemctl suspend` |
| **hibernate** | `shutdown /h` | not a thing on macOS — the word stays an ordinary search there | `systemctl hibernate` |
| **restart** | `shutdown /r /t 0` | `osascript … System Events restart` | `systemctl reboot` |

Windows carries the documented wrinkle that with hibernation enabled, asking for sleep
gets you hibernation: `SetSuspendState` is a request and the power policy decides. Turning
hibernation off behind your back is not Klippy's to do. **The macOS and Linux commands
compile but have not been run**, as with the macOS hotkey — they cannot be tested from
Windows.

Whether a typed line means anything is decided by
[`UnmatchedSearch`](Klippy/Services/UnmatchedSearch.cs), which is pure in the way
[`ExecutionPolicy`](Klippy/Services/ExecutionPolicy.cs) is: the platform is a parameter,
the file system is reached through an injected probe and the environment through another,
so the rules that decide whether
to execute typed text are the ones the tests pin down hardest. A run that fails reports in
the ordinary toast and leaves the window up to be read.

**Desktop only.** The offer is a keyboard gesture in a launcher, and a phone has neither a
shell to hand a path to nor a machine of its own to lock — the same reasoning that keeps
the command MRU off mobile.

## Recent commands (the MRU)

The search box is a command line as much as a filter — a quick-code, then the arguments
that fill its `%P%` — and a command line you have to retype is half a command line. Klippy
remembers the lines that did something and offers them back:

- **`↓` on an empty search box** opens the list at the command you used last. `↑`/`↓`
  browse it, and each line lands in the search box as you reach it, so the row underneath
  already shows what `Enter` will do — the expanded URL, the script with its arguments.
  `Enter` then does what it always does: whatever the row is marked for, a copy or a run.
- **Typing** opens it on what the line could still become. Type `?` and the
  `? cats and dogs` you ran yesterday is there to be taken; matching is on the start of
  the line and ignores case.
- **`Esc`** leaves the list, putting back whatever you had typed before you started
  browsing — and `↑` past the top does the same, which is the way back to the snippet
  list for anyone who opened it by accident.
- **A click** takes the line without running it, so a command can be edited before
  `Enter` sends it.

A *command* is a line that did something: whatever was in the search box at the moment a
snippet was copied or run. Browsing to a row and pressing `Enter` with an empty box
records nothing, because there is no line to recall. A command used again moves back to
the top rather than being duplicated, which is what "most recently used" means, and
`? Cats` is kept apart from `? cats` — they search for different things.

One pair of arrow keys, two lists that could want them. The rule is that the MRU has them
only while it is open, and it is only open when it has something to say: a `↓` on an empty
box, or a typed line that starts a command run before. That is also why matching is a
prefix of the *whole line*, rather than the word-prefix search the snippet list uses —
anything looser would hold the list open, and the arrows with it, over ordinary searches
that were never commands. With it closed, `↑`/`↓` are the list's, exactly as before.

The MRU belongs to the snippet command line: a filter typed against the
[clipboard history](#clipboard-history-windows) is not a command, is not recorded, and
`↓` there steps through the clips as it always did. It is **desktop only**, for the reason
the history is — the gesture is a keyboard one, and a phone has neither the keys to browse
with nor the room for the list they open.

### Settings

In `settings.json`, beside the hotkeys:

```json
{
  "CommandHistoryLimit": 100
}
```

At the limit the oldest commands are dropped. **Zero turns the MRU off** and forgets what
it has already recorded, since the file is a record of what you have been typing and that
is a thing a person is entitled to decline.

Commands live in `commands.json` beside `snippets.json`, as a plain JSON array of strings
so the file can be read and edited by hand. Each is written as it is recorded rather than
on a timer: a command arrives when you press `Enter`, not on every copy made anywhere on
the system, so there is nothing to batch. A line is kept exactly as it was typed — `slf `
is an invocation of `slf` with nothing after it yet, and trimming that space would recall
something subtly different from what was run.

## Markdown snippets (pasting into rich-text editors)

A snippet can be flagged **Markdown** in the editor (FORMAT → Markdown). Copying one
puts *two* flavours on the clipboard at once:

| Flavour | Goes to | Content |
|---|---|---|
| HTML | Rich-text editors — BoldDesk, Outlook, Gmail, Word | Rendered formatting |
| Plain text | Notepad, terminals, code editors | The raw Markdown |

The receiving app picks the flavour it prefers, so the same copy pastes *formatted*
into a helpdesk reply box and *as plain text* into a terminal. Converting Markdown
alone wouldn't achieve this — it's the dual-flavour clipboard write that makes
formatting survive the paste.

Snippets are plain by default, including every existing one, so keys, IBANs and shell
commands are never run through Markdown (`docker system prune -af --volumes` would
otherwise risk becoming an en-dash). Rows show a small `md` marker when a snippet is
Markdown.

Conversion uses [Markdig](https://github.com/xoofx/markdig) with a deliberately
conservative pipeline: bare URLs become links and single newlines become `<br>` (so a
canned reply keeps its shape instead of collapsing into one paragraph). SmartyPants is
*not* enabled, precisely because it rewrites `--` and quotes.

Per platform the HTML lands on the clipboard as `HTML Format` (Windows CF_HTML, with
correct UTF-8 byte offsets), `public.html` (macOS/iOS) or `text/html` (Linux/Android).
If a platform rejects the HTML flavour, the copy silently falls back to plain text.

> **Note:** on Windows the HTML flavour is served through OLE while Klippy is running.
> Copy, then paste with Klippy still open (its normal launcher lifecycle). If Klippy is
> closed before you paste, the plain-text flavour still works.

## Settings

Three preferences change what a copy puts on the clipboard, all about Markdown, two more
say whether a copy dismisses the window, one brings a script's folder up to date before it
runs, three govern running an unmatched search, and one says where the window lands when
you summon it. Open with the **settings** footer link
(`Ctrl/⌘+,`) on desktop, or the sliders button in the mobile header — Android shows no
window chrome, so there is no footer to reach.

| Toggle | On (default) | Off |
|---|---|---|
| **Rich text** | A Markdown snippet copies HTML alongside the plain text | It copies its raw Markdown everywhere — what you want when the target is another Markdown editor |
| **Double spacing** | A blank line between blocks, so composers that strip `<p>` margins (Zendesk) still show the separation | Blocks are joined directly, which suits Outlook and Word — they honour those margins and would otherwise space it twice over |
| **Bare links** (default off) | In the plain-text flavour, `[some words](https://www.qwe.com)` becomes just `https://www.qwe.com` — for apps that paste as plain text (the Zendesk mobile app), where the link syntax would otherwise land verbatim. The HTML flavour keeps its links intact | The plain text is the Markdown source as written |

Two more, desktop only, decide whether a copy dismisses Klippy — the same dismissal
`Esc` does, leaving it resident behind the tray icon. They are separate because the two
halves of the app are used differently: you summon the history to paste one clip and be
gone, while snippets get browsed and copied a couple at a time. Mobile has no launcher to
dismiss to, so the pair is hidden there.

| Toggle | On | Off |
|---|---|---|
| **Close on clip** (default on) | Copying from the clipboard history hides the window, so the app you are pasting into comes straight back to the front | The list stays up |
| **Close on snippet** (default off) | Copying a snippet hides the window too | The list stays up for the next copy |

One, desktop only, says whether running a script or an application
[pulls its folder first](#pull-first) — where what you run is kept in a checkout, this is
what keeps it current.

| Toggle | On | Off (default) |
|---|---|---|
| **Pull first** | `git pull` runs in the script or application's own folder, and is waited for, before it starts | It starts as it is on disk |

Three more, desktop only, govern
[running an unmatched search](#running-an-unmatched-search) — the one feature that executes
rather than copies, which is why all three default to the cautious answer.

| Toggle | On (default) | Off |
|---|---|---|
| **Run it** | A search that matched nothing and names something runnable is offered, and `Enter` runs it | `Enter` does nothing, as it always did on an empty list |
| **Verify paths** | Only a path that is really there is offered | Any rooted path is offered and the OS reports the failure — for a share that is slow to answer, or a path that does not exist yet |
| **Confirm OS actions** | Lock, sleep, hibernate and restart ask first; `Enter` again confirms | They run on the `Enter` that offered them |

In `settings.json`:

```json
{
  "ExecutePullFirst": false,
  "ExecuteUnmatched": true,
  "ExecuteVerifyPaths": true,
  "ExecuteConfirmSystemActions": true
}
```

One more, desktop only, says where the window lands when a key summons it. Klippy is
resident, so by default it comes back exactly where you left it — which is worth keeping
if it always lives in the same corner, since after a week your hand finds the search box
without looking. Less so across a multi-monitor desk, where "where you left it" is often
a screen you have since turned away from.

| Choice | Where the window appears |
|---|---|
| **Where it was** (default) | Wherever it last sat — what Klippy has always done |
| **Centre** | Centred on the screen the pointer is on |
| **Pointer** | Hung from the mouse pointer, dropped far enough that the cursor lands on the search box, and nudged to stay fully on screen |

A summon also brings the window to the **virtual desktop you are on**. A window is assigned
to a desktop when it becomes visible and stays there, so one left showing on another desktop
would otherwise be found rather than summoned: the activate would take you to it instead of
bringing it to you, which is the opposite of what a hotkey means. Klippy hides it first, and
the show that follows lands it where you are. Nothing to configure, and nothing to pay on
the usual path — a dismissed window is already hidden.

It applies whenever Klippy comes back to you — the hotkeys, the tray icon, and the first
appearance at launch — including when the window was left sitting behind whatever you were
working in. It never moves a window that is already in front of you and focused, so
switching between snippets and history with the other hotkey leaves it exactly where it is
rather than throwing it across the desk mid-use. In `settings.json` the key is `SummonPlacement`,
spelled `"Remembered"`, `"Centre"` or `"Pointer"`; anything else reads as `"Remembered"`
rather than costing you the rest of the file.

Each toggle saves as it is flipped, into the same `settings.json` as the hotkeys; there
is no OK button to forget. The panel scrolls rather than running off the bottom of a short
window. The Markdown three default to today's behaviour, so an upgrade changes nothing about
how existing snippets copy — and **Where it was** does the same for the window.

## Export / import

Open with the **export / import** footer link (`Ctrl/⌘+E`) on desktop, or the
transfer button in the mobile header.

- **Export** writes snippets to a JSON file — the whole set or a single tag
  (the scope defaults to whichever tag chip is active). The format is identical
  to the store file, so an export doubles as a backup.
- **Import** first previews the picked file (snippet count and the tags it
  contains), then merges everything or just one tag. Merging never wipes data:
  a snippet with a known id replaces the existing copy, everything else is added.

## Global hotkeys (desktop)

Klippy runs as a resident launcher: it stays alive behind a tray / menu-bar icon, and a
system-wide hotkey summons it. Closing the window hides it rather than quitting; see
[Quitting](#quitting-desktop) for the ways out. Only one instance runs at a time, so
launching Klippy again just tells you it is already resident.

There are two keys, one per half of the app:

| Key (Windows / macOS) | Summons |
|---|---|
| `Ctrl+Alt+K` / `⌥⌘K` | Saved snippets |
| `Ctrl+Alt+J` / `⌥⌘J` | Clipboard history |

Each means *show me this view*. Pressing a key while its view is already in front
dismisses the window, as the single key always did; pressing the **other** key switches
views rather than hiding, which is the point of having two. `Esc` still dismisses once
the filter and any overlay are cleared. Arriving in a view clears the search box, since
a filter typed against snippets means nothing against clips.

They register independently, so one losing the race for its combination leaves the other
working, and Klippy says on stderr which one it could not claim. The history key is only
registered where there is a history to summon — not on mobile, and not with history
switched off.

Stored in `settings.json` next to your snippets:

```json
{
  "Hotkey": "Ctrl+Alt+K",
  "HotkeyEnabled": true,
  "HistoryHotkey": "Ctrl+Alt+J"
}
```

`HotkeyEnabled` is the master switch for both. Setting `HistoryHotkey` to `""` turns off
just that one — it is not filled back in with the default, since that would re-register a
key you had just removed.

Modifiers may be written as Ctrl/Control, Alt/Option, Shift and Cmd/Command/Win/Meta, in
any order and any case; at least one modifier is required, since a bare key would swallow
that keystroke system-wide. If another application already owns the combination, Klippy
says so on stderr and starts without a hotkey rather than failing.

Keeping the app resident has a second benefit on Windows: the HTML clipboard flavour is
served through OLE for as long as Klippy runs, so the "paste before closing Klippy" caveat
above effectively disappears.

Implementation is per-platform, since Avalonia has no global-hotkey API. Each key is a
separate registration on its own [`MessageOnlyWindow`](Klippy.Desktop/MessageOnlyWindow.cs),
which costs an idle thread apiece and buys independent failure:
[`WindowsHotkey`](Klippy.Desktop/WindowsHotkey.cs) uses `RegisterHotKey`, and
[`MacHotkey`](Klippy.Desktop/MacHotkey.cs) uses
Carbon's `RegisterEventHotKey` — chosen over an event tap because it needs no Accessibility
permission. **The macOS path compiles but has not been run**, since it cannot be tested
from Windows.

## Quitting (desktop)

Being resident is the point, so nothing about the window ends Klippy: closing it hides
it, and `Esc` dismisses it. Three things do end it — two of them from the window you are
already looking at:

- **Type `quit`.** It appears as an offer in the band where the first row would have
  been, reading `Quit Klippy · stop listening and leave the tray`, and `↵` takes it —
  the same gesture, in the same place, as [`lock` or `restart`](#running-an-unmatched-search).
- **The `quit` footer link**, for when the pointer is already down there.
- **The tray / menu-bar icon's Quit**, which is where it has always been.

Both of the in-window routes land on the same confirmation, because the search box and a
footer full of links are places a stray keystroke or click can reach: `↵` or **Quit Klippy**
closes, `Esc` or **Cancel** goes back. The tray menu asks nothing and never did — picking
**Quit** off a two-item menu is already a deliberate act.

Nothing is at stake in the data — snippets are written per edit and the clipboard history
flushes on exit — but the hotkeys and the tray icon go with it, and getting them back means
launching Klippy again. That, rather than lost work, is what the dialog says.

**An item that matches beats it**, exactly as one beats `lock`: `quit` is an ordinary word,
and a snippet answering to it was plausibly what was being looked for. A snippet called
"Quit the trial" keeps the word a filter for as long as it exists — the footer link and the
tray are then the ways out, which is why the link is there and not only the word.

It is **not** part of [running an unmatched search](#running-an-unmatched-search), although
it stands in the same place and answers to the same key. Klippy closing is not Klippy
starting something: no plan reaches
[`ProcessLauncher`](Klippy/Services/ProcessLauncher.cs), and it is outside the **Run it**
setting — being unwilling to hand typed text to the machine is no reason to be unable to
close the app. For the same reason its confirmation is not the one **Confirm OS actions**
can waive: that governs the machine's controls, and this one closes a program.

The word is deliberately not offered on mobile: Android leaves closing to the system's own
gesture, and iOS forbids an app quitting itself outright, so there `quit` stays an ordinary
search term.

## Clipboard history (Windows)

Klippy also keeps what you copy. `Ctrl+Alt+J` summons it directly (see
[Global hotkeys](#global-hotkeys-desktop)), or the **History** chip, first in the chip
row, switches the list from saved snippets to captured clips — newest first, searchable with the same
prefix matching, and copied back with the same Enter or click. A clip carries the app it
came from and its age instead of a tag and a quick-code, and keeps whatever flavours it
was captured with, so pasting one back into a rich-text editor gives what the original
copy would have.

### Text, files and images

A clip is one of three kinds, and each pastes back as what it was:

| Kind | Captured from | Pastes as |
|---|---|---|
| Text | `CF_UNICODETEXT`, plus `HTML Format` when offered | text, with formatting where the target takes it |
| Files | `CF_HDROP` | real files in Explorer; the paths in a text editor |
| Image | `PNG` where offered, otherwise `CF_DIB` | `CF_DIB` for a bitmap, `PNG` otherwise |

File clips store paths, not contents, so copying a 4 GB folder costs a few hundred
bytes — and pasting one later fails the same way Explorer would if the files have since
moved. Image clips store the bytes the source app offered, unconverted: PNG when it is
on the clipboard, otherwise the raw DIB with a BMP file header on the front. That is
why Klippy needs no image codec at all.

Image bytes live in `clips/` beside `history.json` rather than inside it, because the
JSON is rewritten whole on every flush and megabytes of base64 would make each copy cost
the entire history. A blob is deleted with its clip, whether that is a delete, a clear
or an eviction. Images above `HistoryImageLimitMb` (16 by default) are not recorded.

Copying a file or image clip back is done through Win32 rather than Avalonia, whose
clipboard cannot express `CF_HDROP` or `CF_DIB` at all. Text and HTML still go through
Avalonia exactly as before — that path is what the Markdown flavours depend on and it
is left alone. One consequence worth knowing: a PNG-flavoured clip goes back as PNG,
which browsers, Office and chat clients accept, but a few paint-style apps that only
speak DIB will not see it.

Per-clip actions, on row hover: **pin** (exempt from eviction), **save as snippet**
(opens the editor prefilled — the clip stays put), **delete**, and **copy**.
A clip carries no
[Execute marker](#running-things-links-scripts-applications-and-macros), so the history
always copies; save a clip as a snippet and mark that if you want to run it. Deleting a
clip asks for no confirmation, unlike deleting a snippet: a clip is transient by nature
and the next copy makes another. The footer's **clear history** empties everything
except pinned clips.

**Windows only, and mobile never.** Capture is `AddClipboardFormatListener` on the same
message-only window the global hotkey uses ([`MessageOnlyWindow`](Klippy.Desktop/MessageOnlyWindow.cs)),
so it costs one shared thread. Android has forbidden background clipboard reads since
API 29 and shows a system toast on any foreground read since API 31; iOS forbids them
outright. There is no polite way around either, so mobile stays a snippet manager and
the History chip is simply absent there. macOS is not wired up yet — it needs a
`NSPasteboard.changeCount` poll, since it has no notification API at all.

### What is deliberately not recorded

Password managers mark their clipboard writes so managers like this one look away, and
[`CapturePolicy`](Klippy/Services/CapturePolicy.cs) honours them: the
`ExcludeClipboardContentFromMonitorProcessing` format that KeePass, 1Password and
Bitwarden set, the older `Clipboard Viewer Ignore` convention, and
`CanIncludeInClipboardHistory` — which is read *by value*, since an app setting it to 1
is opting in rather than out. Klippy's own copies are skipped too, identified by
comparing the clipboard's owning process to its own; without that, every snippet copied
would be echoed straight back into the history.

The policy is a pure function with no Win32 in it, so the security-critical half of
capture is something the tests pin down rather than something you have to trust.

That said, **the history is a plaintext record of what you copied**, protected by the
same `%APPDATA%` permissions as `snippets.json` and nothing more. If that is not a trade
you want, `"HistorySessionOnly": true` keeps it in memory and never writes it to disk.

### Settings

In `settings.json`, next to the hotkey:

```json
{
  "HistoryEnabled": true,
  "HistoryLimit": 500,
  "HistorySessionOnly": false,
  "HistoryCaptureImages": true,
  "HistoryCaptureFiles": true,
  "HistoryImageLimitMb": 16,
  "HistoryExcludedApps": ["keepass", "some-other-app"]
}
```

`HistoryExcludedApps` matches on process name, so `keepass` catches
`C:\Program Files\KeePass\KeePass.exe`. At the limit the oldest unpinned clips are
dropped — but never a pinned one, and never the clip that just arrived, since a history
full of pinned clips would otherwise swallow every new copy in silence.

Clips live in `history.json` beside the snippets, written on a two-second timer and on
exit rather than per copy: history changes on *every* copy anywhere on the system, and
rewriting the file each time would be the wrong shape entirely. The cost is that a hard
crash loses the last couple of seconds — the right trade for data that is itself
transient, and a real difference from `snippets.json`, which is atomic per mutation.

**Not yet done:** paste-back. Selecting a clip copies it; it does not paste it into the
window you came from, which is the thing that makes Ditto feel fast. That needs
`SetForegroundWindow` plus synthesised Ctrl+V, and is the next piece of work.

## Provenance and bulk import

Every snippet carries an optional `Source` and `ExternalId` (e.g. `BoldDesk Aug 2026`
and `13`). Together they give a snippet an identity in the system it came from, so a
later re-import of the same external export **updates snippets in place instead of
duplicating them** — the external system knows nothing about Klippy's own `Guid`.
Klippy's id is preserved on such a match, so it stays stable across re-imports.
Snippets created in Klippy leave both fields empty and continue to match on id alone.

`tools/` holds the BoldDesk pipeline:

```sh
# 1. markdown files + spreadsheet of titles -> a Klippy import file
python tools/bolddesk_to_klippy.py "<export folder>" out.json --source "BoldDesk Aug 2026" --tag support

# 2. merge it into the local store (same Merge path the in-app importer uses)
dotnet run --project tools/Importer -- out.json
```

Re-running both steps is safe: the second pass reports every snippet as *updated*
rather than adding duplicates. Pass the same `--tag` each time — a merge replaces the
whole snippet, so omitting it would blank the tag on snippets that already carry one.

## Where data lives, and backup

Snippets are one JSON file in the platform app-data folder — `%APPDATA%\Klippy\snippets.json`
on Windows, `~/.config/Klippy/` on macOS/Linux, and `files/.config/Klippy/` inside the app
sandbox on Android (mode `0600`, app-private). The whole list is held in memory and each
mutation rewrites the file atomically (temp file + replace), so a crash can't corrupt it.
A copy also writes, because `LastUsedAt` drives recency ranking.

**Android backup is on by default.** `allowBackup="true"` is now set explicitly rather
than relied on as an implicit default, and both rule files
([`backup_rules.xml`](Klippy.Android/Resources/xml/backup_rules.xml) for API ≤30,
[`data_extraction_rules.xml`](Klippy.Android/Resources/xml/data_extraction_rules.xml) for
API 31+) restrict backup to `.config/Klippy/` alone. That include-list matters: debug
fast-deploy drops ~85 MB of Avalonia assemblies into `files/.__override__`, which would
otherwise be swept into the 25 MB Auto Backup quota. A verified backup run transfers
about 7 KB.

**Optional "wipe on uninstall".** Uninstalling always deletes app-private storage, so
what really decides whether snippets come back is the *backup*. The Export/Import panel
has a **DEVICE BACKUP** toggle; turning it off moves the store into Android's `no_backup`
directory, which is excluded from Auto Backup and device-to-device transfer — so an
uninstall genuinely wipes the data. Turning it back on moves it home again. The new copy
is always written before the old one is deleted, so the switch can't lose snippets, and
the active location is inferred from where the file actually is rather than from a
separate setting that could drift out of sync.

The toggle is hidden where the platform has no such concept (desktop). It is **not yet
wired up on iOS**, which needs `NSURLIsExcludedFromBackupKey` on the file rather than a
dedicated directory.

## Solution layout

| Project | Purpose |
|---|---|
| `Klippy` | Shared app: models, JSON store, search, view models, views, theme |
| `Klippy.Desktop` | Windows/macOS/Linux head |
| `Klippy.Android` / `Klippy.iOS` | Mobile heads |
| `Klippy.Tests` | xunit: search/store units + Avalonia headless UI tests |
| `tools/fontfix.cs` | One-shot TTF name-table normalizer (see below) |

Snippets persist as a single JSON file in the platform app-data folder
(`%APPDATA%\Klippy\snippets.json` on Windows), written atomically, serialized with
source-generated `System.Text.Json` (no reflection — AOT/trim safe). MVVM uses
CommunityToolkit.Mvvm source generators; all XAML bindings are compiled.

## Building

```sh
dotnet run --project Klippy.Desktop            # run the desktop app
dotnet test                                    # run tests (also captures artifacts/screenshot-*.png)
dotnet build Klippy.Android -c Debug           # Android (requires android workload)
```

### Release / publish

On Windows use [`build.ps1`](build.ps1), which publishes the desktop head in Release with
**NativeAOT** — no JIT, fast cold start, smallest output:

```powershell
.\build.ps1                                  # NativeAOT, win-x64
.\build.ps1 -Runtime win-arm64 -Clean -Test  # arm64, clean first, run tests
.\build.ps1 -NoAot -Output C:\dist\klippy    # fallback, custom output folder
```

> **Enabling NativeAOT.** It needs the MSVC toolset — the Windows SDK alone is not
> enough. If Visual Studio is already installed, add the single component rather than
> installing a second, standalone Build Tools copy (run elevated, then restart the shell):
>
> ```powershell
> & 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe' modify `
>     --installPath 'C:\Program Files\Microsoft Visual Studio\18\Community' `
>     --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --quiet --norestart
> ```
>
> Add `Microsoft.VisualStudio.Component.VC.Tools.ARM64` as well to publish `win-arm64`.
> The equivalent in the GUI is Visual Studio Installer → Modify → Individual components →
> search "MSVC". `build.ps1` prints the command tailored to your machine.

> **Execution policy.** Windows PowerShell 5.1 refuses unsigned scripts by default. Use
> PowerShell 7 (`pwsh`), or run it as
> `powershell -ExecutionPolicy Bypass -File .\build.ps1`.

> **Code signing (releases).** [`release.ps1`](release.ps1) hands packaging to Parcel,
> which signs the Windows exe, uninstaller and NSIS installer with **Azure Trusted
> Signing**. Nothing that identifies the signing account is in the repo: the build and
> the script load `%USERPROFILE%\.config\shine.env` (a private `KEY=value` file, never
> checked in) and refuse to start unless it holds all six keys —
> `CodeSigning__TenantId`, `CodeSigning__ClientId`, `CodeSigning__ClientSecret` for the
> Entra app registration that has the *Trusted Signing Certificate Profile Signer* role,
> and `CodeSigning__Endpoint`, `CodeSigning__AccountName`,
> `CodeSigning__CertificateProfileName` for the Trusted Signing resource. The checked-in
> `Klippy.Desktop.parcel` knows nothing about signing: the build writes a copy under
> `_build` with the signing block filled in from those keys and packs from that, so a
> hand-run `parcel pack` on the original still gives an unsigned build. A value already
> exported in the shell wins over the file. This exists because an unsigned NSIS
> installer wrapping a native binary trips Defender's `Wacatac.B!ml` heuristic: 1.0.2
> was quarantined on download. `build.ps1` is unaffected — it publishes the bare exe and
> signs nothing.

NativeAOT links with MSVC, so the script checks for the Visual Studio
"Desktop development with C++" workload **before** building. Without that check the
failure only surfaces minutes in, as a bare "Platform linker not found". When the
component is missing it names it, prints the winget command to install it, and points at
`-NoAot`.

`-NoAot` publishes trimmed + ReadyToRun instead: no C++ toolchain needed and still quick
to start, but self-contained and ~54 MB rather than a single small native binary.

Other platforms publish directly:

```sh
dotnet publish Klippy.Desktop -c Release -r osx-arm64    # macOS (run on a Mac)
```

iOS is AOT by nature and trims `SdkOnly` (build on a Mac).

### Android

`build.ps1 -Android` publishes the Android head. It needs a JDK (17+) and the Android
SDK — `ANDROID_HOME`, or the Android Studio default — and checks for both up front:

```powershell
.\build.ps1 -Android                                   # Release, profiled AOT, arm64
.\build.ps1 -Android -Configuration Debug -Install      # quick build, push to device
.\build.ps1 -Android -NoAot                            # skip AOT, much faster to iterate
.\build.ps1 -Android -Abi android-x64                  # emulator
```

`-Install` runs `adb install -r` against the connected device.

Android's AOT is **not** the desktop's NativeAOT. Release turns on Mono *profiled* AOT
(`RunAOTCompilation`, `AndroidEnableProfiledAot`) plus full trimming and
`AndroidStripILAfterAOT`: hot startup paths are precompiled to native code while the Mono
runtime still ships inside the APK. So none of the MSVC toolchain above is involved, and
the size trade runs the *other* way — Release is larger than Debug (~14 MB arm64-only vs
~10 MB), because the precompiled native code outweighs what stripping the IL gives back.
It buys startup time, not size. Use `-NoAot` while iterating.

> **Stale APKs.** Android packaging is incremental and gets it wrong: when an APK is
> already present, MSBuild re-signs the previous package and reports success with zero
> errors even though the assemblies changed — so a "successful" build can silently ship
> stale code. `build.ps1 -Android` deletes the previous packages first to force a real
> repackage (much cheaper than `-Clean`, since the AOT output in `obj/` is reused), and
> warns if the APK it produced predates the build. Running `dotnet publish` on the
> project by hand does **not** protect you from this.

> **Signing.** No keystore is configured, so APKs are signed with the shared Android
> debug key. That is fine for sideloading, but they are not distributable, and a later
> release-signed build will not install over one without uninstalling first.
> `ApplicationId` is also still the template's `com.CompanyName.Klippy`.

## Design

`design_handoff_klippy/` holds the high-fidelity design reference (HTML mock + README
with the token sheet). The Avalonia theme mirrors it: tokens live in
[`App.axaml`](Klippy/App.axaml), control styles in
[`Styles/KlippyStyles.axaml`](Klippy/Styles/KlippyStyles.axaml), icons as
`StreamGeometry` in [`Styles/Icons.axaml`](Klippy/Styles/Icons.axaml).

### Fonts

Chivo and Chivo Mono (OFL) are bundled under `Klippy/Assets/Fonts`. The static TTFs
shipped by the foundry carry per-weight family names ("Chivo SemiBold"), which breaks
weight-based matching in Avalonia/Skia. `tools/fontfix.cs` rewrites the name tables so
all weights share one family; the committed fonts are already normalized. If you ever
replace them, re-run:

```sh
dotnet run tools/fontfix.cs -- Klippy/Assets/Fonts
```
