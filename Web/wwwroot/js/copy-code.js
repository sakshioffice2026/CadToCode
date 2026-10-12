(function () {
    "use strict";

    var KEYWORDS = [
        "abstract", "as", "base", "break", "case", "catch", "class", "const", "continue",
        "default", "do", "else", "enum", "event", "explicit", "extern", "finally", "for",
        "foreach", "get", "if", "implicit", "in", "init", "interface", "internal", "is",
        "namespace", "new", "operator", "out", "override", "params", "partial", "private",
        "protected", "public", "readonly", "record", "ref", "required", "return", "sealed",
        "set", "static", "struct", "switch", "this", "throw", "try", "using", "virtual",
        "void", "while"
    ];

    var BUILTIN_TYPES = [
        "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object",
        "sbyte", "short", "string", "uint", "ulong", "ushort", "var",
        "List", "Dictionary", "IEnumerable", "DateTime", "Guid"
    ];

    var LITERALS = ["true", "false", "null"];

    var DECLARATION_WORDS = ["class", "struct", "record", "enum", "interface", "new"];

    var TOKEN_PATTERN = /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|("(?:[^"\\\n]|\\.)*")|(\b\d+(?:\.\d+)?[mMfFdDlLuU]?\b)|([A-Za-z_][A-Za-z0-9_]*)/g;

    function toSet(list) {
        var set = {};
        list.forEach(function (item) {
            set[item] = true;
        });
        return set;
    }

    var keywordSet = toSet(KEYWORDS);
    var typeSet = toSet(BUILTIN_TYPES);
    var literalSet = toSet(LITERALS);
    var declarationSet = toSet(DECLARATION_WORDS);

    function appendText(parent, text) {
        if (text.length > 0) {
            parent.appendChild(document.createTextNode(text));
        }
    }

    function appendToken(parent, text, className) {
        var span = document.createElement("span");
        span.className = className;
        span.textContent = text;
        parent.appendChild(span);
    }

    function highlightElement(element) {
        var source = element.textContent || "";
        var fragment = document.createDocumentFragment();
        var lastIndex = 0;
        var previousWord = "";
        var match;

        TOKEN_PATTERN.lastIndex = 0;

        while ((match = TOKEN_PATTERN.exec(source)) !== null) {
            appendText(fragment, source.substring(lastIndex, match.index));
            lastIndex = match.index + match[0].length;

            if (match[1]) {
                appendToken(fragment, match[0], "tok-comment");
            } else if (match[2]) {
                appendText(fragment, match[0]);
            } else if (match[3]) {
                appendToken(fragment, match[0], "tok-number");
            } else {
                var word = match[4];

                if (literalSet[word]) {
                    appendToken(fragment, word, "tok-bool");
                } else if (keywordSet[word]) {
                    appendToken(fragment, word, "tok-keyword");
                } else if (typeSet[word] || declarationSet[previousWord]) {
                    appendToken(fragment, word, "tok-type");
                } else {
                    appendText(fragment, word);
                }

                previousWord = word;
                continue;
            }

            previousWord = "";
        }

        appendText(fragment, source.substring(lastIndex));

        element.textContent = "";
        element.appendChild(fragment);
    }

    function copyWithFallback(text) {
        return new Promise(function (resolve, reject) {
            var area = document.createElement("textarea");
            area.value = text;
            area.setAttribute("readonly", "");
            area.style.position = "fixed";
            area.style.top = "-1000px";
            document.body.appendChild(area);
            area.select();

            try {
                var ok = document.execCommand("copy");
                document.body.removeChild(area);
                if (ok) {
                    resolve();
                } else {
                    reject(new Error("Copy command failed"));
                }
            } catch (error) {
                document.body.removeChild(area);
                reject(error);
            }
        });
    }

    function copyText(text) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text).catch(function () {
                return copyWithFallback(text);
            });
        }
        return copyWithFallback(text);
    }

    function notify(message) {
        if (window.CadToCode && typeof window.CadToCode.showToast === "function") {
            window.CadToCode.showToast(message);
        }
    }

    function initCopyButtons() {
        document.querySelectorAll("[data-cc-copy-target]").forEach(function (button) {
            var defaultLabel = button.getAttribute("data-cc-copy-label") || button.textContent.trim();
            var resetTimer = null;

            button.addEventListener("click", function () {
                var target = document.querySelector(button.getAttribute("data-cc-copy-target"));
                if (!target) {
                    return;
                }

                copyText(target.textContent || "")
                    .then(function () {
                        button.textContent = "Copied";
                        button.classList.add("is-copied");
                        notify("Code copied to clipboard.");
                    })
                    .catch(function () {
                        notify("Could not copy. Select the code and copy it manually.");
                    })
                    .then(function () {
                        if (resetTimer) {
                            window.clearTimeout(resetTimer);
                        }
                        resetTimer = window.setTimeout(function () {
                            button.textContent = defaultLabel;
                            button.classList.remove("is-copied");
                        }, 1800);
                    });
            });
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("[data-cc-highlight]").forEach(highlightElement);
        initCopyButtons();
    });
})();
