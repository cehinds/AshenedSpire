// The browser's file doors (Ashen.Platform.WebFiles): save a text file the way the web game does (a Blob behind a synthetic anchor,
// src/ui/screens/profileArchive.js entryCard, profileNotice.js, about.js, ui/components/offlinePlay.js saveFile) and let the player pick one
// (the hidden <input type=file> of offlinePlay.js, read as text). A pick is polled from C#: 0 idle, 1 waiting, 2 ready, 3 cancelled.
mergeInto(LibraryManager.library, {
  AshenDownloadText: function (fileName, text, mime) {
    var name = UTF8ToString(fileName);
    var blob = new Blob([UTF8ToString(text)], { type: UTF8ToString(mime) });
    var url = URL.createObjectURL(blob);
    var link = document.createElement('a');
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  },

  AshenPickFileOpen: function (accept) {
    var state = Module.ashenPick || (Module.ashenPick = { state: 0, text: '' });
    state.state = 1;
    state.text = '';
    var input = document.createElement('input');
    input.type = 'file';
    input.accept = UTF8ToString(accept);
    input.style.display = 'none';
    input.addEventListener('change', function () {
      var file = input.files && input.files[0];
      if (!file) { state.state = 3; return; }
      var reader = new FileReader();
      reader.onload = function () { state.text = String(reader.result); state.state = 2; };
      reader.onerror = function () { state.state = 3; };
      reader.readAsText(file);
    });
    input.addEventListener('cancel', function () { state.state = 3; });
    document.body.appendChild(input);
    input.click();
    setTimeout(function () { if (input.parentNode) input.parentNode.removeChild(input); }, 60000);
  },

  AshenPickFileState: function () {
    var state = Module.ashenPick;
    if (!state) return 0;
    var now = state.state;
    if (now === 3) state.state = 0;
    return now;
  },

  AshenPickFileText: function () {
    var state = Module.ashenPick;
    var text = state ? state.text : '';
    if (state) { state.state = 0; state.text = ''; }
    var size = lengthBytesUTF8(text) + 1;
    var buffer = _malloc(size);
    stringToUTF8(text, buffer, size);
    return buffer;
  }
});
