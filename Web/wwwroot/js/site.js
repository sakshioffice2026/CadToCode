(function () {
    "use strict";

    var ALLOWED_EXTENSIONS = [".dxf", ".png", ".jpg", ".jpeg"];
    var MAX_FILE_SIZE_MB = 20;
    var MAX_FILES_PER_BATCH = 20;
    var toastTimer = null;

    window.CadToCode = window.CadToCode || {};

    window.CadToCode.showToast = function (message, durationMs) {
        var toast = document.getElementById("cc-toast");
        if (!toast) {
            return;
        }

        toast.textContent = message;
        toast.hidden = false;

        if (toastTimer) {
            window.clearTimeout(toastTimer);
        }

        toastTimer = window.setTimeout(function () {
            toast.hidden = true;
        }, durationMs || 2200);
    };

    function getExtension(fileName) {
        var index = fileName.lastIndexOf(".");
        return index < 0 ? "" : fileName.substring(index).toLowerCase();
    }

    function formatSize(bytes) {
        if (bytes < 1024) {
            return bytes + " B";
        }
        if (bytes < 1024 * 1024) {
            return (bytes / 1024).toFixed(1) + " KB";
        }
        return (bytes / (1024 * 1024)).toFixed(1) + " MB";
    }

    function validateFile(file) {
        if (ALLOWED_EXTENSIONS.indexOf(getExtension(file.name)) === -1) {
            return "Only DXF, PNG and JPG files are supported.";
        }
        if (file.size > MAX_FILE_SIZE_MB * 1024 * 1024) {
            return "The file is larger than " + MAX_FILE_SIZE_MB + " MB.";
        }
        if (file.size === 0) {
            return "The file is empty.";
        }
        return null;
    }

    function initDropzone() {
        var zone = document.querySelector("[data-cc-dropzone]");
        if (!zone) {
            return;
        }

        var input = zone.querySelector("[data-cc-dropzone-input]");
        var nameLabel = zone.querySelector("[data-cc-file-name]");
        var fileList = zone.querySelector("[data-cc-file-list]");
        var form = zone.closest("form");
        var submit = form ? form.querySelector("[data-cc-submit]") : null;
        var submitLabel = submit ? submit.textContent : "";

        function clearSelection() {
            input.value = "";
            zone.classList.remove("has-file");
            nameLabel.hidden = true;
            nameLabel.textContent = "";

            if (fileList) {
                fileList.hidden = true;
                fileList.innerHTML = "";
            }

            updateSubmitLabel(0);
        }

        function updateSubmitLabel(count) {
            if (!submit) {
                return;
            }

            submit.textContent = count > 1
                ? "Upload and analyze " + count + " drawings"
                : submitLabel;
        }

        function applyFiles(files) {
            var accepted = [];
            var skipped = [];

            Array.prototype.forEach.call(files, function (file) {
                var error = validateFile(file);
                if (error) {
                    skipped.push(file.name + ": " + error);
                } else {
                    accepted.push(file);
                }
            });

            if (accepted.length > MAX_FILES_PER_BATCH) {
                skipped.push("Only the first " + MAX_FILES_PER_BATCH + " files were kept.");
                accepted = accepted.slice(0, MAX_FILES_PER_BATCH);
            }

            if (accepted.length === 0) {
                clearSelection();
                if (skipped.length > 0) {
                    window.CadToCode.showToast(skipped[0], 3600);
                }
                return;
            }

            try {
                var transfer = new DataTransfer();
                accepted.forEach(function (file) {
                    transfer.items.add(file);
                });
                input.files = transfer.files;
            } catch (e) {
                window.CadToCode.showToast("Could not update the selection. Please click to browse.", 3200);
                return;
            }

            showSelection(accepted);

            if (skipped.length > 0) {
                window.CadToCode.showToast(skipped.length + " file(s) skipped. " + skipped[0], 4200);
            }
        }

        function showSelection(files) {
            var total = files.reduce(function (sum, file) {
                return sum + file.size;
            }, 0);

            nameLabel.textContent = files.length === 1
                ? files[0].name + " (" + formatSize(files[0].size) + ")"
                : files.length + " files selected (" + formatSize(total) + ")";
            nameLabel.hidden = false;
            zone.classList.add("has-file");

            if (fileList) {
                fileList.innerHTML = "";

                if (files.length > 1) {
                    files.forEach(function (file) {
                        var item = document.createElement("li");
                        item.textContent = file.name + " (" + formatSize(file.size) + ")";
                        fileList.appendChild(item);
                    });
                    fileList.hidden = false;
                } else {
                    fileList.hidden = true;
                }
            }

            updateSubmitLabel(files.length);
        }

        input.addEventListener("change", function () {
            if (input.files && input.files.length > 0) {
                applyFiles(input.files);
            } else {
                clearSelection();
            }
        });

        ["dragenter", "dragover"].forEach(function (eventName) {
            zone.addEventListener(eventName, function (event) {
                event.preventDefault();
                zone.classList.add("is-dragover");
            });
        });

        ["dragleave", "dragend"].forEach(function (eventName) {
            zone.addEventListener(eventName, function () {
                zone.classList.remove("is-dragover");
            });
        });

        zone.addEventListener("drop", function (event) {
            event.preventDefault();
            zone.classList.remove("is-dragover");

            var files = event.dataTransfer ? event.dataTransfer.files : null;
            if (!files || files.length === 0) {
                return;
            }

            applyFiles(files);
        });

        if (form) {
            form.addEventListener("submit", function (event) {
                if (!input.files || input.files.length === 0) {
                    event.preventDefault();
                    window.CadToCode.showToast("Please choose at least one file first.", 2600);
                    return;
                }

                if (input.files.length > MAX_FILES_PER_BATCH) {
                    event.preventDefault();
                    window.CadToCode.showToast("You can upload up to " + MAX_FILES_PER_BATCH + " files at a time.", 3200);
                    return;
                }

                for (var i = 0; i < input.files.length; i++) {
                    var error = validateFile(input.files[i]);
                    if (error) {
                        event.preventDefault();
                        window.CadToCode.showToast(input.files[i].name + ": " + error, 3600);
                        return;
                    }
                }

                if (submit) {
                    submit.disabled = true;
                    submit.textContent = input.files.length > 1
                        ? "Analyzing " + input.files.length + " drawings..."
                        : "Analyzing...";
                }
            });
        }
    }

    function setRowEditing(row, editing) {
        row.querySelectorAll("[data-cc-view]").forEach(function (element) {
            element.hidden = editing;
        });
        row.querySelectorAll("[data-cc-edit]").forEach(function (element) {
            element.hidden = !editing;
        });
        row.classList.toggle("is-editing", editing);
    }

    function resetRowInputs(row) {
        row.querySelectorAll("input").forEach(function (input) {
            if (input.type === "checkbox") {
                input.checked = input.defaultChecked;
            } else {
                input.value = input.defaultValue;
            }
        });
    }

    function initRowEditing() {
        var rows = document.querySelectorAll("[data-cc-row]");
        if (rows.length === 0) {
            return;
        }

        rows.forEach(function (row) {
            var startButton = row.querySelector("[data-cc-edit-start]");
            var cancelButton = row.querySelector("[data-cc-edit-cancel]");

            if (startButton) {
                startButton.addEventListener("click", function () {
                    rows.forEach(function (other) {
                        if (other !== row && other.classList.contains("is-editing")) {
                            resetRowInputs(other);
                            setRowEditing(other, false);
                        }
                    });

                    setRowEditing(row, true);

                    var firstInput = row.querySelector("input[data-cc-edit]:not([type=checkbox])");
                    if (firstInput) {
                        firstInput.focus();
                        firstInput.select();
                    }
                });
            }

            if (cancelButton) {
                cancelButton.addEventListener("click", function () {
                    resetRowInputs(row);
                    setRowEditing(row, false);
                });
            }

            row.addEventListener("keydown", function (event) {
                if (event.key === "Escape" && row.classList.contains("is-editing")) {
                    resetRowInputs(row);
                    setRowEditing(row, false);
                }
            });
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        initDropzone();
        initRowEditing();
    });
})();