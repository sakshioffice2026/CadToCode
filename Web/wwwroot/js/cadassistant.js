(function () {
    "use strict";

    var root = document.querySelector("[data-ca-root]");
    if (!root) {
        return;
    }

    /* ---------- Config ---------- */

    var projectId = parseInt(root.getAttribute("data-ca-project"), 10) || 0;
    var pageKind = root.getAttribute("data-ca-page") || "workspace";
    var openByDefault = root.getAttribute("data-ca-open-default") === "true";
    var urls = {
        chat: root.getAttribute("data-ca-chat-url"),
        history: root.getAttribute("data-ca-history-url"),
        reset: root.getAttribute("data-ca-reset-url"),
        status: root.getAttribute("data-ca-status-url"),
        upload: root.getAttribute("data-ca-upload-url")
    };
    var maxText = parseInt(root.getAttribute("data-ca-max-text"), 10) || 4000;
    var maxFiles = parseInt(root.getAttribute("data-ca-max-files"), 10) || 5;
    var maxBytes = parseInt(root.getAttribute("data-ca-max-bytes"), 10) || 10485760;

    var TEXT_EXTENSIONS = [".dxf", ".txt", ".cs", ".json", ".csv", ".step", ".stp"];
    var BINARY_EXTENSIONS = [".png", ".jpg", ".jpeg"];

    var SESSION_KEY = "cadassistant:session:" + projectId;
    var OPEN_KEY = "cadassistant:open";

    /* ---------- Elements ---------- */

    var thread = root.querySelector("[data-ca-thread]");
    var emptyState = root.querySelector("[data-ca-empty]");
    var statusChip = root.querySelector("[data-ca-status]");
    var composer = root.querySelector("[data-ca-composer]");
    var dropHint = root.querySelector("[data-ca-drop]");
    var input = root.querySelector("[data-ca-input]");
    var sendButton = root.querySelector("[data-ca-send]");
    var attachButton = root.querySelector("[data-ca-attach]");
    var fileInput = root.querySelector("[data-ca-file]");
    var attachmentBox = root.querySelector("[data-ca-attachments]");
    var drawingType = root.querySelector("[data-ca-drawing-type]");
    var filterInput = root.querySelector("[data-ca-filter]");
    var includeCode = root.querySelector("[data-ca-include-code]");
    var toggles = document.querySelectorAll("[data-ca-toggle]");
    var editorInput = document.getElementById("cc-code-input");
    var dropInput = document.querySelector("[data-cc-dropzone-input]");
    var dropZone = document.querySelector("[data-cc-dropzone]");

    /* ---------- State ---------- */

    var sessionId = readStore(SESSION_KEY) || "";
    var attachments = [];
    var busy = false;
    var controller = null;
    var liveNode = null;
    var liveTimer = null;
    var lastSend = null;
    var historyLoaded = false;
    var dragDepth = 0;
    var contextFiles = [];
    var contextPending = false;
    var contextSignature = "";

    /* ---------- Helpers ---------- */

    function readStore(key) {
        try {
            return window.sessionStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    function writeStore(key, value) {
        try {
            if (value === null || value === "") {
                window.sessionStorage.removeItem(key);
            } else {
                window.sessionStorage.setItem(key, value);
            }
        } catch (e) { /* storage unavailable */ }
    }

    function notify(message) {
        if (window.CadToCode && typeof window.CadToCode.showToast === "function") {
            window.CadToCode.showToast(message);
        }
    }

    function antiForgeryToken() {
        var field = document.querySelector("input[name='__RequestVerificationToken']");
        return field ? field.value : "";
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined && text !== null) {
            node.textContent = text;
        }
        return node;
    }

    function extensionOf(name) {
        var index = name.lastIndexOf(".");
        return index < 0 ? "" : name.substring(index).toLowerCase();
    }

    function scrollToEnd() {
        thread.scrollTop = thread.scrollHeight;
    }

    function postJson(url, body, signal) {
        return fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "Accept": "application/json",
                "RequestVerificationToken": antiForgeryToken()
            },
            body: JSON.stringify(body),
            signal: signal
        });
    }

    function postForm(url, form, signal) {
        return fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Accept": "application/json",
                "RequestVerificationToken": antiForgeryToken()
            },
            body: form,
            signal: signal
        });
    }

    /* ---------- Syntax highlighting ---------- */

    var KEYWORDS = toSet(["as", "break", "case", "catch", "class", "const", "continue", "default", "do", "else", "for",
        "foreach", "if", "in", "is", "new", "null", "private", "public", "return", "static", "switch", "this", "throw",
        "try", "using", "void", "while"]);
    var TYPES = toSet(["bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "string", "var",
        "List", "Dictionary", "StepDocument", "Solid", "CustomProfile", "Math", "Console", "Path"]);
    var LITERALS = toSet(["true", "false", "null"]);
    var TOKEN_PATTERN = /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|("(?:[^"\\\n]|\\.)*")|(\b\d+(?:\.\d+)?[mMfFdDlLuU]?\b)|([A-Za-z_][A-Za-z0-9_]*)/g;

    function toSet(list) {
        var set = {};
        list.forEach(function (item) { set[item] = true; });
        return set;
    }

    function highlight(target, source) {
        var fragment = document.createDocumentFragment();
        var last = 0;
        var match;

        TOKEN_PATTERN.lastIndex = 0;

        while ((match = TOKEN_PATTERN.exec(source)) !== null) {
            if (match.index > last) {
                fragment.appendChild(document.createTextNode(source.substring(last, match.index)));
            }
            last = match.index + match[0].length;

            var cls = null;
            if (match[1]) {
                cls = "tok-comment";
            } else if (match[3]) {
                cls = "tok-number";
            } else if (match[4]) {
                if (LITERALS[match[4]]) {
                    cls = "tok-bool";
                } else if (KEYWORDS[match[4]]) {
                    cls = "tok-keyword";
                } else if (TYPES[match[4]]) {
                    cls = "tok-type";
                }
            }

            if (cls) {
                fragment.appendChild(el("span", cls, match[0]));
            } else {
                fragment.appendChild(document.createTextNode(match[0]));
            }
        }

        if (last < source.length) {
            fragment.appendChild(document.createTextNode(source.substring(last)));
        }

        target.textContent = "";
        target.appendChild(fragment);
    }

    /* ---------- Open / close ---------- */

    function setOpen(open, keepFocus) {
        root.classList.toggle("is-open", open);
        root.setAttribute("aria-hidden", open ? "false" : "true");
        document.body.classList.toggle("ca-open", open);

        Array.prototype.forEach.call(toggles, function (button) {
            button.setAttribute("aria-expanded", open ? "true" : "false");
        });

        writeStore(OPEN_KEY, open ? "1" : "0");

        if (open) {
            checkStatus();
            loadHistory();
            if (!keepFocus) {
                window.setTimeout(function () { input.focus(); }, 60);
            }
        }
    }

    function isOpen() {
        return root.classList.contains("is-open");
    }

    /* ---------- Status ---------- */

    function setChip(text, state, title) {
        statusChip.textContent = text;
        statusChip.classList.toggle("ca-chip-muted", state === "muted");
        statusChip.classList.toggle("ca-chip-bad", state === "bad");
        statusChip.title = title || "";
    }

    function checkStatus() {
        fetch(urls.status, { credentials: "same-origin", headers: { "Accept": "application/json" } })
            .then(function (response) { return response.json(); })
            .then(function (status) {
                if (status.available) {
                    setChip("Model ready" + (status.model ? " \u00B7 " + status.model : ""), "ok", "Running offline on this machine");
                } else {
                    setChip("Model offline", "bad", status.message || "The local model is not available.");
                }
            })
            .catch(function () {
                setChip("Status unknown", "muted", "Could not check the model.");
            });
    }

    /* ---------- Rendering ---------- */

    function hideEmpty() {
        if (emptyState) {
            emptyState.hidden = true;
        }
    }

    function showEmpty() {
        if (emptyState) {
            emptyState.hidden = false;
        }
    }

    function fileChip(name, onRemove) {
        var chip = el("span", "ca-file");
        chip.appendChild(el("span", "ca-file-name", name));

        if (onRemove) {
            var remove = el("button", "ca-file-remove", "\u00D7");
            remove.type = "button";
            remove.setAttribute("aria-label", "Remove " + name);
            remove.addEventListener("click", onRemove);
            chip.appendChild(remove);
        }

        return chip;
    }

    function addUser(text, names) {
        hideEmpty();

        var wrap = el("div", "ca-msg ca-msg-user");

        if (names && names.length > 0) {
            var files = el("div", "ca-files");
            names.forEach(function (name) { files.appendChild(fileChip(name)); });
            wrap.appendChild(files);
        }

        wrap.appendChild(el("div", "ca-bubble", text));
        thread.appendChild(wrap);
        scrollToEnd();
    }

    function addError(text) {
        hideEmpty();

        var wrap = el("div", "ca-msg ca-msg-assistant ca-msg-error");
        wrap.appendChild(el("div", "ca-bubble", text));
        thread.appendChild(wrap);
        scrollToEnd();
    }

    function addNote(text, names) {
        hideEmpty();

        var wrap = el("div", "ca-msg ca-msg-note");
        wrap.appendChild(el("div", "ca-bubble", text));

        if (names && names.length > 0) {
            var files = el("div", "ca-files");
            names.forEach(function (name) { files.appendChild(fileChip(name)); });
            wrap.appendChild(files);
        }

        thread.appendChild(wrap);
        scrollToEnd();
    }

    function renderList(title, items) {
        var box = el("div", "ca-vision");
        box.appendChild(el("div", "ca-vision-title", title));

        var list = el("ul", "ca-vision-list");
        items.forEach(function (text) { list.appendChild(el("li", "", text)); });
        box.appendChild(list);

        return box;
    }

    function renderVision(message, wrap) {
        var views = (message.views || []).map(function (v) {
            return v.description ? v.name + ": " + v.description : v.name;
        });

        if (views.length > 0) {
            wrap.appendChild(renderList("Views seen in the image", views));
        }

        var unverified = (message.parameters || []).filter(function (p) {
            return p.verified === false && p.value !== null && p.value !== undefined;
        });

        if (unverified.length > 0) {
            var readings = unverified.map(function (p) {
                return p.key + " = " + p.value + " " + p.unit + (p.evidence ? " (read: " + p.evidence + ")" : "");
            });

            var box = renderList("Unverified readings from the image", readings);
            box.classList.add("ca-vision-warn");
            box.insertBefore(el("span", "ca-badge-unverified", "Unverified \u2013 please confirm"), box.firstChild.nextSibling);
            wrap.appendChild(box);
        }

        var notes = message.uncertainties || [];

        if (notes.length > 0) {
            wrap.appendChild(renderList("Unclear or not used", notes));
        }
    }

    function addAssistant(message) {
        hideEmpty();

        var wrap = el("div", "ca-msg ca-msg-assistant");
        wrap.appendChild(el("div", "ca-bubble", message.content || ""));
        renderVision(message, wrap);

        var blocks = message.codeBlocks || [];
        var actions = message.actions || [];

        blocks.forEach(function (block) {
            wrap.appendChild(renderCode(block));

            var row = el("div", "ca-actions");

            actions
                .filter(function (action) { return action.targetCodeBlockId === block.id; })
                .forEach(function (action) { row.appendChild(actionButton(action, block)); });

            if (row.childNodes.length > 0) {
                wrap.appendChild(row);
            }
        });

        if (message.status) {
            var verified = message.status.indexOf("Verified") === 0;
            wrap.appendChild(el("div", "ca-status " + (verified ? "ca-status-ok" : "ca-status-warn"),
                (verified ? "\u2713 " : "\u26A0 ") + message.status));
        }

        var general = actions.filter(function (action) { return !action.targetCodeBlockId; });
        if (general.length > 0) {
            var generalRow = el("div", "ca-actions");
            general.forEach(function (action) { generalRow.appendChild(actionButton(action, null)); });
            wrap.appendChild(generalRow);
        }

        var meta = [];
        if (message.model) {
            meta.push(message.model);
        }
        if (message.elapsedMs) {
            meta.push((message.elapsedMs / 1000).toFixed(1) + " s");
        }
        if (meta.length > 0) {
            wrap.appendChild(el("div", "ca-meta", meta.join(" \u00B7 ")));
        }

        thread.appendChild(wrap);
        scrollToEnd();
    }

    function renderCode(block) {
        var details = el("details", "ca-code");
        var summary = el("summary");
        summary.appendChild(el("span", "ca-code-title", block.title || "STEP script"));
        summary.appendChild(el("span", "ca-code-lines", (block.lineCount || 0) + " lines"));
        details.appendChild(summary);

        var pre = el("pre");
        var code = el("code");
        pre.tabIndex = 0;
        pre.appendChild(code);
        details.appendChild(pre);

        highlight(code, block.code || "");
        return details;
    }

    function actionButton(action, block) {
        var button = el("button", "ca-action" + (action.kind === "run" ? " ca-action-primary" : ""), action.label);
        button.type = "button";
        button.addEventListener("click", function () { handleAction(action, block); });
        return button;
    }

    /* ---------- Actions ---------- */

    function copyText(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            return navigator.clipboard.writeText(text);
        }

        return new Promise(function (resolve, reject) {
            var area = document.createElement("textarea");
            area.value = text;
            area.style.position = "fixed";
            area.style.opacity = "0";
            document.body.appendChild(area);
            area.select();

            try {
                document.execCommand("copy") ? resolve() : reject(new Error("copy failed"));
            } catch (e) {
                reject(e);
            } finally {
                document.body.removeChild(area);
            }
        });
    }

    function applyCode(code) {
        if (!editorInput) {
            notify("The script editor is not available on this page.");
            return false;
        }

        editorInput.value = code;
        editorInput.dispatchEvent(new Event("input", { bubbles: true }));
        return true;
    }

    var scriptBusy = false;

    function ensureStepName(name) {
        var fileName = (name || "model.step").trim();
        if (!/\.(step|stp)$/i.test(fileName)) {
            fileName += ".step";
        }
        return fileName;
    }

    function saveStep(content, name) {
        var blob = new Blob([content], { type: "model/step" });
        var url = URL.createObjectURL(blob);
        var link = document.createElement("a");
        link.href = url;
        link.download = ensureStepName(name);
        link.style.display = "none";
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        window.setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
    }

    function runDirect(code) {
        if (scriptBusy) {
            return;
        }

        scriptBusy = true;
        notify("Running script\u2026");

        fetch("/Project/RunCode", {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "Accept": "application/json",
                "RequestVerificationToken": antiForgeryToken()
            },
            body: JSON.stringify({ code: code })
        })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("The server returned " + response.status + ".");
                }
                return response.json();
            })
            .then(function (result) {
                if (!result.success) {
                    var first = (result.diagnostics && result.diagnostics.length > 0)
                        ? result.diagnostics[0].message
                        : (result.message || "Run failed.");
                    notify("Run failed: " + first);
                    return;
                }

                if (result.stepContent) {
                    saveStep(result.stepContent, result.fileName);
                    notify("Downloaded " + ensureStepName(result.fileName));
                } else {
                    notify("Script ran successfully.");
                }
            })
            .catch(function (error) {
                notify(error && error.message ? error.message : "Could not reach the server.");
            })
            .then(function () {
                scriptBusy = false;
            });
    }

    function editorButton(selector) {
        return document.querySelector("[data-cc-sandbox][data-cc-source=\"#cc-code-input\"] " + selector);
    }

    function handleAction(action, block) {
        switch (action.kind) {
            case "copy":
                copyText(block.code).then(
                    function () { notify("Code copied."); },
                    function () { notify("Copy failed."); });
                break;

            case "apply":
                if (applyCode(block.code)) {
                    notify("Script applied to the editor.");
                    editorInput.scrollIntoView({ behavior: "smooth", block: "center" });
                }
                break;

            case "run":
                if (!editorInput) {
                    runDirect(block.code);
                    break;
                }
                if (applyCode(block.code)) {
                    var run = editorButton("[data-cc-run]");
                    if (run) {
                        notify("Running script\u2026");
                        run.click();
                        editorInput.scrollIntoView({ behavior: "smooth", block: "center" });
                    }
                }
                break;

            case "download-step":
                if (!editorInput) {
                    runDirect(block.code);
                    break;
                }
                if (applyCode(block.code)) {
                    var download = editorButton("[data-cc-download]");
                    if (download) {
                        download.click();
                    }
                }
                break;

            case "regenerate":
                if (lastSend) {
                    send(lastSend);
                }
                break;
        }
    }

    /* ---------- Attachments ---------- */

    function renderAttachments() {
        attachmentBox.textContent = "";

        attachments.forEach(function (item, index) {
            attachmentBox.appendChild(fileChip(item.fileName, function () {
                attachments.splice(index, 1);
                renderAttachments();
            }));
        });
    }

    function addFiles(list) {
        Array.prototype.forEach.call(list, function (file) {
            if (attachments.length >= maxFiles) {
                notify("You can attach up to " + maxFiles + " files.");
                return;
            }

            var ext = extensionOf(file.name || "");
            var isText = TEXT_EXTENSIONS.indexOf(ext) !== -1;
            var isBinary = BINARY_EXTENSIONS.indexOf(ext) !== -1;

            if (!isText && !isBinary) {
                notify("Unsupported file: " + file.name);
                return;
            }

            if (file.size > maxBytes) {
                notify(file.name + " is too large.");
                return;
            }

            var entry = {
                fileName: file.name || "pasted-file" + ext,
                contentType: file.type || "",
                sizeBytes: file.size,
                textContent: null
            };

            if (isBinary || ext === ".dxf") {
                entry.file = file;
            }

            if (!isText) {
                attachments.push(entry);
                renderAttachments();
                return;
            }

            var reader = new FileReader();
            reader.onload = function () {
                entry.textContent = String(reader.result || "").substring(0, maxText);
                attachments.push(entry);
                renderAttachments();
            };
            reader.onerror = function () { notify("Could not read " + file.name + "."); };
            reader.readAsText(file.slice(0, maxText * 2));
        });
    }

    /* ---------- Sending ---------- */

    function setBusy(state) {
        busy = state;
        sendButton.textContent = state ? "Stop" : "Send";
        sendButton.classList.toggle("is-stop", state);
    }

    function startLive(analysingImage) {
        var started = Date.now();
        var working = analysingImage
            ? "Analysing image with the local vision model\u2026 "
            : "Thinking on your local model\u2026 ";

        liveNode = el("div", "ca-msg ca-msg-assistant");
        var row = el("div", "ca-live");
        row.appendChild(el("span", "ca-spinner"));
        var label = el("span", "", "Reading drawing data\u2026");
        row.appendChild(label);
        liveNode.appendChild(row);
        thread.appendChild(liveNode);
        scrollToEnd();

        liveTimer = window.setInterval(function () {
            var seconds = Math.floor((Date.now() - started) / 1000);
            label.textContent = seconds < 2
                ? "Reading drawing data\u2026"
                : working + seconds + " s";
        }, 500);
    }

    function stopLive() {
        if (liveTimer) {
            window.clearInterval(liveTimer);
            liveTimer = null;
        }

        if (liveNode && liveNode.parentNode) {
            liveNode.parentNode.removeChild(liveNode);
        }

        liveNode = null;
    }

    function plainEntry(item) {
        return {
            fileName: item.fileName,
            contentType: item.contentType,
            sizeBytes: item.sizeBytes,
            textContent: item.textContent
        };
    }

    function isImageName(name) {
        return /\.(png|jpe?g)$/i.test(name || "");
    }

    function send(options) {
        if (busy) {
            return;
        }

        var replay = !!options;
        var prompt = (replay ? options.prompt : input.value).trim();
        var files = replay ? options.attachments : attachments.slice();

        if (!replay && contextPending && contextFiles.length > 0) {
            files = contextFiles.concat(files).slice(0, maxFiles);
        }

        if (!prompt && files.length === 0) {
            return;
        }

        var type = replay ? options.drawingType : drawingType.value;
        var filter = replay ? options.filterInstructions : filterInput.value.trim();

        lastSend = { prompt: prompt, attachments: files, drawingType: type, filterInstructions: filter };

        var payload = {
            projectId: projectId,
            sessionId: sessionId || null,
            prompt: prompt,
            drawingType: type || null,
            filterInstructions: filter || null,
            currentCode: includeCode && includeCode.checked && editorInput ? editorInput.value : null,
            attachments: files.map(plainEntry)
        };

        var uploadFiles = files.filter(function (f) {
            return f.file && (isImageName(f.fileName) || /\.dxf$/i.test(f.fileName || ""));
        });
        var useUpload = !!urls.upload && uploadFiles.some(function (f) { return isImageName(f.fileName); });

        addUser(prompt || "Attached files", files.map(function (f) { return f.fileName; }));

        if (!replay) {
            contextPending = false;
            input.value = "";
            attachments = [];
            renderAttachments();
            autoGrow();
        }

        setBusy(true);
        startLive(files.some(function (f) { return /\.(png|jpe?g)$/i.test(f.fileName || ""); }));
        controller = new AbortController();

        var request;

        if (useUpload) {
            var form = new FormData();

            uploadFiles.forEach(function (f) { form.append("files", f.file, f.fileName); });
            form.append("projectId", String(projectId));

            if (sessionId) {
                form.append("sessionId", sessionId);
            }

            form.append("prompt", prompt);

            if (type) {
                form.append("drawingType", type);
            }

            if (payload.currentCode) {
                form.append("currentCode", payload.currentCode);
            }

            request = postForm(urls.upload, form, controller.signal);
        } else {
            request = postJson(urls.chat, payload, controller.signal);
        }

        request
            .then(function (response) {
                return response.json().then(
                    function (data) { return data; },
                    function () { return null; });
            })
            .then(function (data) {
                if (data && data.sessionId) {
                    sessionId = data.sessionId;
                    writeStore(SESSION_KEY, sessionId);
                }

                stopLive();

                if (data && data.success && data.message) {
                    addAssistant(data.message);

                    if (data.warnings && data.warnings.length > 0) {
                        addNote(data.warnings.join(" "));
                    }
                } else {
                    addError((data && data.error) || "The server could not answer this request.");
                    checkStatus();
                }
            })
            .catch(function (error) {
                stopLive();

                if (error && error.name === "AbortError") {
                    addError("Request stopped.");
                } else {
                    addError("Could not reach the server.");
                }
            })
            .then(function () {
                controller = null;
                setBusy(false);
                input.focus();
            });
    }

    function stop() {
        if (controller) {
            controller.abort();
        }
    }

    /* ---------- History / reset ---------- */

    function loadHistory() {
        if (historyLoaded || !sessionId) {
            return;
        }

        historyLoaded = true;

        fetch(urls.history + "?projectId=" + encodeURIComponent(projectId) + "&sessionId=" + encodeURIComponent(sessionId),
            { credentials: "same-origin", headers: { "Accept": "application/json" } })
            .then(function (response) { return response.json(); })
            .then(function (data) {
                (data.messages || []).forEach(function (message) {
                    if (message.role === "user") {
                        addUser(message.content, message.attachments || []);
                    } else {
                        addAssistant(message);
                    }
                });
            })
            .catch(function () { /* history is optional */ });
    }

    function resetChat() {
        if (busy) {
            stop();
        }

        var previous = sessionId;

        contextFiles = [];
        contextPending = false;
        contextSignature = "";

        thread.querySelectorAll(".ca-msg").forEach(function (node) { node.remove(); });
        attachments = [];
        renderAttachments();
        showEmpty();

        if (!previous) {
            return;
        }

        postJson(urls.reset, { projectId: projectId, sessionId: previous })
            .then(function (response) { return response.json(); })
            .then(function (data) {
                sessionId = data && data.sessionId ? data.sessionId : "";
                writeStore(SESSION_KEY, sessionId);
            })
            .catch(function () {
                sessionId = "";
                writeStore(SESSION_KEY, "");
            });
    }

    /* ---------- Input behaviour ---------- */

    function autoGrow() {
        input.style.height = "auto";
        input.style.height = Math.min(input.scrollHeight, 144) + "px";
    }

    input.addEventListener("input", autoGrow);

    input.addEventListener("keydown", function (event) {
        if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
            event.preventDefault();
            send();
        }
    });

    input.addEventListener("paste", function (event) {
        var files = event.clipboardData ? event.clipboardData.files : null;

        if (files && files.length > 0) {
            event.preventDefault();
            addFiles(files);
        }
    });

    sendButton.addEventListener("click", function () {
        if (busy) {
            stop();
        } else {
            send();
        }
    });

    attachButton.addEventListener("click", function () { fileInput.click(); });

    fileInput.addEventListener("change", function () {
        addFiles(fileInput.files);
        fileInput.value = "";
    });

    root.querySelectorAll("[data-ca-suggest]").forEach(function (button) {
        button.addEventListener("click", function () {
            input.value = button.getAttribute("data-ca-suggest");
            autoGrow();
            input.focus();
        });
    });

    root.querySelector("[data-ca-reset]").addEventListener("click", resetChat);
    root.querySelector("[data-ca-close]").addEventListener("click", function () { setOpen(false); });

    Array.prototype.forEach.call(toggles, function (button) {
        button.addEventListener("click", function () { setOpen(!isOpen()); });
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape" && isOpen() && root.contains(document.activeElement)) {
            setOpen(false);
        }
    });

    /* ---------- Drag and drop ---------- */

    function hasFiles(event) {
        var types = event.dataTransfer ? event.dataTransfer.types : null;
        return !!types && Array.prototype.indexOf.call(types, "Files") !== -1;
    }

    root.addEventListener("dragenter", function (event) {
        if (!hasFiles(event)) {
            return;
        }

        event.preventDefault();
        dragDepth++;
        composer.classList.add("is-dragging");
        dropHint.hidden = false;
    });

    root.addEventListener("dragover", function (event) {
        if (hasFiles(event)) {
            event.preventDefault();
        }
    });

    root.addEventListener("dragleave", function (event) {
        if (!hasFiles(event)) {
            return;
        }

        dragDepth = Math.max(0, dragDepth - 1);

        if (dragDepth === 0) {
            composer.classList.remove("is-dragging");
            dropHint.hidden = true;
        }
    });

    root.addEventListener("drop", function (event) {
        if (!hasFiles(event)) {
            return;
        }

        event.preventDefault();
        dragDepth = 0;
        composer.classList.remove("is-dragging");
        dropHint.hidden = true;
        addFiles(event.dataTransfer.files);
    });

    /* ---------- Upload bridge ---------- */

    function formatSize(bytes) {
        if (bytes >= 1048576) {
            return (bytes / 1048576).toFixed(1) + " MB";
        }

        if (bytes >= 1024) {
            return Math.max(1, Math.round(bytes / 1024)) + " KB";
        }

        return bytes + " B";
    }

    function signatureOf(list) {
        return Array.prototype.map.call(list, function (file) {
            return file.name + ":" + file.size + ":" + (file.lastModified || 0);
        }).join("|");
    }

    function buildContextEntry(file, done) {
        var ext = extensionOf(file.name || "");
        var entry = {
            fileName: file.name,
            contentType: file.type || "",
            sizeBytes: file.size,
            textContent: null
        };

        if (BINARY_EXTENSIONS.indexOf(ext) !== -1 || ext === ".dxf") {
            entry.file = file;
        }

        if (ext !== ".dxf" || file.size > maxBytes) {
            done(entry);
            return;
        }

        var reader = new FileReader();
        reader.onload = function () {
            entry.textContent = String(reader.result || "").substring(0, maxText);
            done(entry);
        };
        reader.onerror = function () { done(entry); };
        reader.readAsText(file.slice(0, maxText * 2));
    }

    function logUploadSelection(list) {
        var files = Array.prototype.slice.call(list || []);
        var signature = signatureOf(files);

        if (signature === contextSignature) {
            return;
        }

        contextSignature = signature;

        if (files.length === 0) {
            contextFiles = [];
            contextPending = false;
            return;
        }

        var entries = [];
        var remaining = files.length;

        files.forEach(function (file, index) {
            buildContextEntry(file, function (entry) {
                entries[index] = entry;
                remaining--;

                if (remaining > 0 || signature !== contextSignature) {
                    return;
                }

                contextFiles = entries.slice(0, maxFiles);
                contextPending = true;

                var names = entries.map(function (item) {
                    return item.fileName + " (" + formatSize(item.sizeBytes) + ")";
                });

                var extra = entries.length > maxFiles
                    ? " Only the first " + maxFiles + " are shared with the chat."
                    : "";

                addNote(
                    entries.length === 1
                        ? "1 drawing selected for upload. Ask a question about it."
                        : entries.length + " drawings selected for upload. Ask a question about them." + extra,
                    names);

                if (pageKind === "upload" && !isOpen()) {
                    setOpen(true, true);
                }
            });
        });
    }

    function syncUploadFiles(fallback) {
        window.setTimeout(function () {
            var list = dropInput && dropInput.files && dropInput.files.length > 0 ? dropInput.files : fallback;
            logUploadSelection(list || []);
        }, 0);
    }

    if (dropInput) {
        dropInput.addEventListener("change", function () { syncUploadFiles(null); });
    }

    if (dropZone) {
        dropZone.addEventListener("drop", function (event) {
            syncUploadFiles(event.dataTransfer ? event.dataTransfer.files : null);
        });
    }

    /* ---------- Init ---------- */

    autoGrow();

    var storedOpen = readStore(OPEN_KEY);

    if (storedOpen === "1" || (storedOpen === null && openByDefault)) {
        setOpen(true, true);
    }
}());