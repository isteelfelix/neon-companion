mergeInto(LibraryManager.library, {
  NeonWebSpeech_IsAvailable: function () {
    var hasRecognition = !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    var hasTts = !!(window.speechSynthesis && window.SpeechSynthesisUtterance);
    return (hasRecognition || hasTts) ? 1 : 0;
  },

  NeonWebSpeech_StartRecognition: function (goNamePtr) {
    var goName = UTF8ToString(goNamePtr);
    var SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!SR) return;

    try {
      var rec = new SR();
      rec.lang = navigator.language || "en-US";
      rec.interimResults = false;
      rec.maxAlternatives = 1;
      rec.onresult = function (event) {
        var text = event && event.results && event.results[0] && event.results[0][0]
          ? event.results[0][0].transcript
          : "";
        if (text) SendMessage(goName, "OnWebSpeechRecognized", text);
      };
      rec.onend = function () { SendMessage(goName, "OnWebSpeechEnded", ""); };
      window.__neonSpeechRecognition = rec;
      rec.start();
    } catch (e) {
      console.warn("WebSpeech start failed", e);
    }
  },

  NeonWebSpeech_StopRecognition: function () {
    try {
      if (window.__neonSpeechRecognition) window.__neonSpeechRecognition.stop();
    } catch (e) {
      console.warn("WebSpeech stop failed", e);
    }
  },

  NeonWebSpeech_Speak: function (textPtr, goNamePtr) {
    var text = UTF8ToString(textPtr);
    var goName = UTF8ToString(goNamePtr);
    if (!window.speechSynthesis || !window.SpeechSynthesisUtterance) {
      SendMessage(goName, "OnWebTtsComplete", "");
      return;
    }

    try {
      var utterance = new SpeechSynthesisUtterance(text);
      utterance.lang = navigator.language || "en-US";
      utterance.onend = function () { SendMessage(goName, "OnWebTtsComplete", ""); };
      utterance.onerror = function () { SendMessage(goName, "OnWebTtsComplete", ""); };
      window.__neonSpeechSynthesisUtterance = utterance;
      window.speechSynthesis.speak(utterance);
    } catch (e) {
      console.warn("WebSpeech speak failed", e);
      SendMessage(goName, "OnWebTtsComplete", "");
    }
  },

  NeonWebSpeech_StopSpeaking: function () {
    try {
      if (window.speechSynthesis) window.speechSynthesis.cancel();
    } catch (e) {
      console.warn("WebSpeech stop speaking failed", e);
    }
  }
});
