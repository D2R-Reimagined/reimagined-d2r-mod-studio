# Reimagined D2R Mod Studio

### So why the hell does this tool exist?
Well, there are a few reasons. First off I can't stand the amount of merge conclicts that happen on the game's text files when trying to work in a shared space. It becomes very difficult to view diffs and track changes. Having to juggle multiple tools and constantly navigate the windows explorer made me wanna scream as well

### What were the goals of this project?

* Handles ALL file types that exist in d2r modding. Json, txt, d1s, etc. (Relative tools are opened for each, level editor for example is opened for you when editing those files upon confirmation)
* * I did not want to have to jump between different tools to different thing. You still need to for level editing, sprite editing, etc but this will serve as the entry point to all of that.
* A "Source Directory" and "Deployment Directory" (Store source files, and your mod deployment files in different area. Deploy/Play will bundle and move for you)
* * The ability to seperate your mod install directory and your source directories allows you to add additional files that don't belong in your mod directory. 
* A single "Play" button from within the editor. With options (D2RLoader, vanilla, whatever)
* Fully handles linting and checks for you.
* * Runs the D2RLint tool automatically and keeps your files in check
* Adding in common calculations
* * Hate having to go figure out what the hell the end Health of a monster is? Or what the skill description is? Yea me too. Hopefully this solves that
* Item tooltip rendering in certain views (Hover unique row in unique editor, and see the built item - or error if incorrect)
* New file system - Move away from the .txt files for much better collaboration and less merge conflicts
* Built-in Git (Git tab beside the project tree, Ctrl+K): tick the files to include, commit, push, pull, switch branches, view diffs and history - driven by your installed `git`, so SSH keys and credential managers just work
