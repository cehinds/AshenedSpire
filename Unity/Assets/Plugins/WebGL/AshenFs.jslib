// Flushes the in-memory file system (persistentDataPath, i.e. the save folder) to the browser's IndexedDB.
// Called from Ashen.Platform.WebFileSystem. One flush runs at a time; a request made meanwhile re-runs it after.
mergeInto(LibraryManager.library, {
  AshenFsSync: function () {
    if (typeof FS === 'undefined' || typeof FS.syncfs !== 'function') return;
    var state = Module.ashenFsState || (Module.ashenFsState = { running: false, again: false });
    if (state.running) { state.again = true; return; }
    function flush() {
      state.running = true;
      FS.syncfs(false, function (err) {
        state.running = false;
        if (err) console.warn('Ashen: IndexedDB sync failed', err);
        if (state.again) { state.again = false; flush(); }
      });
    }
    flush();
  }
});
