---
icon: lucide/braces
description: "Local defines in klippy.vars — resolution order, arguments and flavours."
---

# Variables

A snippet is the same text on every machine; a path is not. `klippy.vars` holds the
per-machine half, so one snippet can be right on a Mac and on Windows:

```ini
# klippy.vars — local defines, never exported
ws=%localappdata%\Programs\WebStorm\bin\webstorm64.exe
src=D:\src
```

A snippet written as `%ws% %src%\myapp` then means the real command line for whichever
machine you are on: copy it and paste it at a prompt, or mark it
[Execute](running-things.md) and press Enter.

**Only names the file defines are ever replaced.** Everything else is left byte for byte
as it was written: `50% off`, `LIKE '%foo%'`, `%%1` in a batch file, `%TEMP%` in a command
you have kept for years. There is no escape syntax because none is needed, and until you
create the file nothing about copying changes at all.

The rest of the rules are short:

- `name=value`, one per line. `#` and `;` start a whole-line comment; a line that is not a
  define is skipped rather than rejected, so one typo costs one variable.
- The value is everything after the first `=`, trimmed — **quotes included**. Quote a path
  with spaces, because the shell you paste into will need them. Running takes a pair that
  wraps the whole value back off: arguments are passed as arguments and .NET supplies
  whatever quoting the OS needs, so a pair left in would reach the program as part of the
  name it is looking for. Copying keeps them, which is what you quoted them for.
- Names are case-insensitive (`%ws%` and `%WS%` are one variable) and may not contain
  whitespace or a percent sign, since neither could be written as `%name%`. A colon is
  ordinary, and is what [flavours](#one-name-two-flavours) are written with.
- A value may use other variables, and anything the file does not define falls through to
  the **process environment** — which is what makes the `%localappdata%` line above resolve
  to a real path. On the copy path that fallback is confined to values: a *snippet*
  containing `%TEMP%` may predate Klippy's variables entirely and has to keep meaning what
  it says. To publish an environment variable to snippets, name it:
  `localappdata=%localappdata%` is a cycle against the file, so it resolves to the one the
  OS has.
- `%C%` and `%P%` are [macros](running-things.md#macros), not variables. A file that defines `c` or `p`
  does not get to swallow them — nobody writing `%C%` meant a variable named C.

## When it happens, and in what order

Expansion is **on use, not on save**. The store keeps `%ws%`, which is the whole point — a
snippet flattened at save time would only ever be right on the machine that last saved it,
and an export would carry one machine's paths to another. Clips from the clipboard history
are never expanded either; captured text is not something you authored.

Variables resolve **before** [macros](running-things.md#macros), on both routes:

| Route | What resolves |
|---|---|
| **Copy** | Variables over the whole text, then `%C%` and `%P%` |
| **Execute** | Variables over the whole line, [environment variables](running-things.md#environment-variables) over the first word, then `%C%` and `%P%` |
| **Either** | A typed argument that [names one](#variables-as-arguments), as the line is read — before it fills a `%P%` |

That order is the point rather than an accident: `%ws%` is a name the item asked to have
resolved, while a clipboard value is data — and data is never re-read for names. A path off
the clipboard keeps its middle, exactly as
[Environment variables](running-things.md#environment-variables) describes. A typed argument is the one
thing read for a name at all, and only ever as a whole word: see
[Variables as arguments](#variables-as-arguments) for where that line is drawn and why.

On the run path the variables file simply sits in front of the machine's own environment,
so `%ws%` names WebStorm there the way `%LOCALAPPDATA%` names a folder — on a marked item
and on a line [typed into the search box](unmatched-search.md) alike, since two
routes to the same launcher should not disagree about what a name means. Where the file and
the environment both define one, the file wins: being the local answer is what it is for.

The one place the two part company is an **argument**. The machine's names resolve in the
first word only, because a child process inherits the environment and can read its own
`%APPDATA%` anyway — so `%TEMP%\build` reaches a script meaning what it says, and the
[`.bat` refusal](running-things.md#what-running-something-will-not-do) goes on seeing what
`cmd.exe` would see. Nothing downstream has ever heard of `klippy.vars`, so a define has no
such second chance: `%z% %app%\notes.txt` resolves `%app%` where it stands, exactly as
copying the same line has always done. One snippet, one meaning, whichever key you press.

The row itself keeps showing `%ws%`, with only its typed arguments filled in. Unlike a
`%P%`, whose value you have just typed and want to check, a variable's value is the same
every time and is usually a long path — and the row is what you would edit. An argument
that named a define *does* show its value there, for the same reason: it is the half you
have just typed and want to check.

## Variables as arguments

A define is as useful on the other side of a quick-code. Say `klippy.vars` holds

```ini
r=%localappdata%\Programs\Rider\bin\rider64.exe
app=D:\src\myapp\MyApp.sln
```

and a snippet marked Execute holds `%r% %P%` behind the quick-code `r`. Then

```
r app
```

starts Rider on that solution. `%r%` is the item's own name for the exe; `app` is an
argument that names the file. The row shows `%r% D:\src\myapp\MyApp.sln` while you type
it, so a name that was *not* found is visible as itself rather than as a launch that opens
the wrong thing.

The rule is deliberately narrow:

- **The whole argument, or nothing.** `app` is a name. `%src%\myapp` is a path that happens
  to mention one, and keeps its percent signs exactly as it always has — which is what lets
  a `%TEMP%\build` reach a script meaning what it says. Only a word that *is* the name is
  looked up, written bare or in full as `%app%`: with nothing either side of it there is
  nothing to delimit it from, so the two read the same. An *item* whose own text says
  `%src%\myapp` is the other case entirely, and does resolve: an item's text is something
  you authored, while a word typed at the prompt is data — and data is only ever read for
  a name it plainly *is*.
- **The file's names, never the machine's.** `%PATH%` has no business arriving as an
  argument because somebody typed `path`. The environment answers for a *value* in the file
  and for the first word of something you run; an argument is neither.
- **Quote it and it is the word itself.** `r "app"` passes `app`. That is the escape for the
  day a define collides with something you meant to search for: `? src` googles `D:\src`
  while `? "src"` googles "src". The quotes cost nothing to spend this way — a name can hold
  no whitespace, so it never needed them to stay one argument, and a *value* with a space in
  it resolves after the line has been split and stays one argument without them.
- **Or say it once, on the item.** `%P:exact%` takes its argument exactly as typed at every
  invocation, so a snippet reading `https://www.google.com/search?q=%P:exact%` googles `src`
  however many paths the file names. The same answer the quotes give, reached from the other
  side: the item settles it for good, the quotes for one line, and where both are used they
  agree. Per placeholder rather than per item — `%P:exact% %P%` resolves the second word and
  not the first.
- **A name that is not defined is passed as typed**, so nothing changes until you define one
  that collides. The quick-code itself is never resolved either, or a file defining `r`
  would put the item behind `r` out of reach of the very line that invokes it.
- `%C%` and `%P%` are still [macros](running-things.md#macros): an argument of `%C%` is the text `%C%`, not a
  define called C.

This is the one place Klippy reads data for a name, and the asymmetry with `%C%` is the
whole of the reason. A clipboard value is what the machine handed over; an argument is a
word somebody stood at the prompt and typed, and asking for it by name is the only thing
they can have meant by it. A copy and a run read the same line, so both get the same
answer — and a `.bat` still refuses an argument `cmd.exe` would re-read, judged on the
value that is about to be passed rather than on the short name it arrived by.

## One name, two flavours

`klippy` is the folder to some items and the solution file to others, and both want to be
typed as `klippy`. So define it twice:

```ini
# klippy.vars
klippy=D:\main\Klippy
klippy=D:\main\Klippy\Klippy.slnx
```

and let the item say which of them it means:

| Item | Typed | Opens |
|---|---|---|
| `%code% %P:folder%` | `c klippy` | `D:\main\Klippy` |
| `%r% %P:file%` | `r klippy` | `D:\main\Klippy\Klippy.slnx` |

A name given more than once keeps every line. On its own it means the **last** of them — a
file read top to bottom ends on the answer — and the flavour is what reaches the others.

An item asks for one either way round. `%P:file%` is the question put to a word somebody is
about to type; `%klippy:file%` is the item having already decided, and is written into its
text like any other name:

```
%code% %klippy:folder%\src
```

opens that folder whichever order the file happens to be in, which a bare `%klippy%` would
not. You can name the flavour at the prompt instead, `r klippy:file`, which **overrules**
the item: its qualifier is a default for whoever invokes it, not a veto on what they ask
for. `%C%` takes no qualifier — a clipboard value is text that has already been fetched,
with nothing left to choose between.

**Which line is the file is read off the value, not off the disk**: a dot in the last
segment names a file, and anything else names a folder. That is how a person reading the
file tells them apart, and it costs no disk access on a keystroke, answers the same on a
machine where the checkout is not cloned yet, and cannot stall on a share that is not
there.

Where that reading would be wrong — a file with no extension, a folder with a dot in its
name — put the flavour in the name instead. An explicit define is found first and settles
it, and it is also how you invent flavours of your own, since `file` and `folder` are the
only two Klippy can work out for itself:

```ini
klippy:folder=D:\main\node_modules.bak
klippy:file=D:\main\Makefile
klippy:docs=D:\main\docs\README.md
```

A colon is an ordinary character in a name, so `%klippy:file%` works in a snippet and in
another define's value exactly as any name does — and so does one Klippy worked out for
itself, so `%app:folder%` answers from an `app` given twice with no `app:folder=` line
anywhere. The count under the Settings box is of lines rather than names — three defines,
however many of them share one.

The rest of the rules:

- **The bare name still answers** where a flavour cannot be found — for a `%P:file%`, that
  is. Against a plain `app=…` it finds `app`, so putting a qualifier on an item never stops
  it working with the defines that have only the one meaning. A `%app:file%` an item wrote
  out is the other case, and is taken at its word the way a flavour typed at the prompt is:
  it names exactly the line it wants, and stays as written where nothing answers.
- **A flavour named at the prompt is taken at its word.** `r klippy:docs` looks for exactly
  that, and passes `klippy:docs` as typed if nothing answers — Klippy does not quietly hand
  back `klippy` when you asked for one of its flavours.
- **A colon does not make a flavour.** `C:\temp` and `https://example.com` are looked up
  whole, find nothing, and are passed as typed, under a `%P:file%` or not: their tails are
  no flavour Klippy knows. A qualifier carries no colon of its own, so where one ends is
  never a matter of opinion.
- **Where one `%P%` swallows the rest**, its flavour covers everything it swallows — the
  last placeholder takes every argument still unused, so they are all asked for on its
  terms.
- **`exact` is not a flavour**, and is the one qualifier the prompt does not overrule.
  `%P:file%` names the meaning it wants; `%P:exact%` says the word has no meaning to want,
  so `r klippy:file` against one passes `klippy:file` itself — an item that promises to hand
  on what you typed would be worth nothing if a line could talk it out of that. It is also
  the one flavour name you cannot invent, though a define written as `x:exact` still answers
  to `%x:exact%` in a snippet like any other name.

## Getting at the file

The **FILES** block in Settings does the whole job, because hunting down `%APPDATA%` to
edit a file you have just been told about is an errand a settings screen should spare you.
It gives [the snippets and this file](settings.md#files) a box each — the only two worth
pointing anywhere — and this is the one with the buttons:

- **The path is a text box.** An empty one means `klippy.vars` in the data folder. A bare
  name sits beside the snippets, and an absolute path is taken as given — which is the
  answer when a snippet store is shared between two machines and the variables file must
  not be. It saves when you leave the box, and applies to the next copy: the file is
  re-read whenever it changes, so this is the one path here with nothing to restart.
- **Create / Open** opens the file in whatever your machine opens a text file with,
  writing a commented example first when there is nothing there yet. The example is
  entirely comments, so a file made by accident defines nothing and changes nothing.
- **Folder** opens the folder it lives in, and the data folder above it has its own
  **Open**.

Under the box, what Klippy read back: `3 variables`, or `no file yet`, or
`no variables in it` for a file that is all comments. That line is how you check a
hand-edit parsed.

The same setting by hand, for anyone who would rather — empty, or left out altogether, is
`klippy.vars` in the data folder:

```json
{
  "VariablesFile": "%USERPROFILE%\\klippy.vars"
}
```

The buttons are desktop only — they go through the same launcher an item marked Execute
does, and mobile has none, so there they are absent rather than dead.
