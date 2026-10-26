(function () {
    "use strict";

    var form = document.getElementById("cc-zoo-form");

    if (!form) {
        return;
    }

    var POLL_MS = 4000;
    var MAX_POLL_MS = 10 * 60 * 1000;
    var MAX_FAILURES = 3;

    var promptInput = document.getElementById("cc-zoo-prompt");
    var formatSelect = document.getElementById("cc-zoo-format");
    var submitButton = document.getElementById("cc-zoo-submit");
    var counter = document.getElementById("cc-zoo-count");
    var errorEl = document.getElementById("cc-zoo-error");

    var resultPanel = document.getElementById("cc-zoo-result");
    var statusEl = document.getElementById("cc-zoo-status");
    var resultPrompt = document.getElementById("cc-zoo-result-prompt");
    var resultError = document.getElementById("cc-zoo-result-error");
    var filesEl = document.getElementById("cc-zoo-files");
    var codeWrap = document.getElementById("cc-zoo-code-wrap");
    var codeEl = document.getElementById("cc-zoo-code");
    var copyButton = document.getElementById("cc-zoo-copy");

    var generateUrl = form.getAttribute("data-generate-url");
    var statusUrl = form.getAttribute("data-status-url");
    var downloadUrl = form.getAttribute("data-download-url");

    var pollTimer = null;
    var pollStartedAt = 0;
    var failures = 0;

    function antiForgeryToken() {
        var field = form.querySelector("input[name='__RequestVerificationToken']");
        return field ? field.value : "";
    }

    function setText(el, text) {
        el.textContent = text || "";
    }

    function showFormError(message) {
        setText(errorEl, message);
        errorEl.hidden = !message;
    }

    function setBusy(busy) {
        submitButton.disabled = busy;
        promptInput.readOnly = busy;
        formatSelect.disabled = busy;
        submitButton.textContent = busy ? "Generating..." : "Generate model";
    }

    var lastPreviewedJob = "";
    var viewerPanel = document.getElementById("cc-zoo-viewer-panel");

    function fileUrl(id, name) {
        return downloadUrl + "?id=" + encodeURIComponent(id) + "&name=" + encodeURIComponent(name);
    }

    function isStep(name) {
        return /\.(step|stp)$/i.test(name || "");
    }

    function previewStep(id, name, scroll) {
        if (!window.CcZooViewer) {
            return;
        }

        lastPreviewedJob = id;
        window.CcZooViewer.loadUrl(fileUrl(id, name), name);

        if (scroll && viewerPanel) {
            viewerPanel.scrollIntoView({ behavior: "smooth", block: "start" });
        }
    }

    document.addEventListener("click", function (event) {
        var button = event.target.closest ? event.target.closest("[data-zoo-view]") : null;

        if (!button) {
            return;
        }

        previewStep(button.getAttribute("data-id"), button.getAttribute("data-name"), true);
    });

    function statusLabel(status) {
        switch (status) {
            case "completed": return "Done";
            case "failed": return "Failed";
            case "in_progress": return "Generating";
            default: return "Queued";
        }
    }

    function renderJob(job) {
        resultPanel.hidden = false;

        setText(statusEl, statusLabel(job.status));
        statusEl.className = "cc-zoo-badge cc-zoo-badge-" + job.status;
        setText(resultPrompt, job.prompt);

        resultError.hidden = !job.error;
        setText(resultError, job.error);

        filesEl.textContent = "";

        var stepFile = null;

        (job.files || []).forEach(function (file) {
            var item = document.createElement("li");
            var link = document.createElement("a");

            link.className = "cc-btn cc-btn-primary cc-btn-small";
            link.href = fileUrl(job.id, file.name);
            link.textContent = "Download " + file.name;

            item.appendChild(link);

            if (isStep(file.name)) {
                stepFile = stepFile || file;

                var view = document.createElement("button");
                view.type = "button";
                view.className = "cc-btn cc-btn-ghost cc-btn-small";
                view.textContent = "View 3D";
                view.addEventListener("click", function () {
                    previewStep(job.id, file.name, true);
                });
                item.appendChild(view);
            }

            filesEl.appendChild(item);
        });

        if (job.status === "completed" && stepFile && lastPreviewedJob !== job.id) {
            previewStep(job.id, stepFile.name, false);
        }

        if (job.kcl) {
            codeEl.textContent = job.kcl;
            codeWrap.hidden = false;
        } else {
            codeEl.textContent = "";
            codeWrap.hidden = true;
        }
    }

    function stopPolling() {
        if (pollTimer) {
            clearTimeout(pollTimer);
            pollTimer = null;
        }
    }

    function finish() {
        stopPolling();
        setBusy(false);
    }

    function schedulePoll(id) {
        pollTimer = setTimeout(function () {
            poll(id);
        }, POLL_MS);
    }

    function poll(id) {
        if (Date.now() - pollStartedAt > MAX_POLL_MS) {
            showFormError("Zoo is taking longer than expected. Check Recent generations later.");
            finish();
            return;
        }

        fetch(statusUrl + "?id=" + encodeURIComponent(id), {
            credentials: "same-origin",
            headers: { "Accept": "application/json" }
        })
            .then(function (response) { return response.json(); })
            .then(function (result) {
                failures = 0;

                if (!result.success) {
                    showFormError(result.errorMessage || "The status could not be read.");
                    finish();
                    return;
                }

                renderJob(result.job);

                if (result.job.status === "completed" || result.job.status === "failed") {
                    finish();
                    return;
                }

                schedulePoll(id);
            })
            .catch(function () {
                failures += 1;

                if (failures >= MAX_FAILURES) {
                    showFormError("The connection was lost. Check Recent generations later.");
                    finish();
                    return;
                }

                schedulePoll(id);
            });
    }

    promptInput.addEventListener("input", function () {
        setText(counter, String(promptInput.value.length));

        if (promptInput.value.trim()) {
            showFormError("");
        }
    });

    form.addEventListener("submit", function (event) {
        event.preventDefault();

        var prompt = promptInput.value.trim();

        if (!prompt) {
            showFormError("Describe the part you want to generate.");
            promptInput.focus();
            return;
        }

        showFormError("");
        stopPolling();
        setBusy(true);

        fetch(generateUrl, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "Accept": "application/json",
                "RequestVerificationToken": antiForgeryToken()
            },
            body: JSON.stringify({ prompt: prompt, outputFormat: formatSelect.value })
        })
            .then(function (response) { return response.json(); })
            .then(function (result) {
                if (!result.success) {
                    showFormError(result.errorMessage || "The request could not be sent.");
                    setBusy(false);
                    return;
                }

                renderJob(result.job);
                pollStartedAt = Date.now();
                failures = 0;
                schedulePoll(result.job.id);
            })
            .catch(function () {
                showFormError("The request could not be sent. Please try again.");
                setBusy(false);
            });
    });

    if (copyButton) {
        copyButton.addEventListener("click", function () {
            if (!navigator.clipboard) {
                return;
            }

            navigator.clipboard.writeText(codeEl.textContent || "").then(function () {
                copyButton.textContent = "Copied";

                window.setTimeout(function () {
                    copyButton.textContent = "Copy";
                }, 1500);
            });
        });
    }
}());
