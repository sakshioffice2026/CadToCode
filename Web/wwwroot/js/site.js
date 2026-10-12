(function () {
    "use strict";

    var ALLOWED_EXTENSIONS = [".dxf", ".png", ".jpg", ".jpeg"];
    var MAX_FILE_SIZE_MB = 20;
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
        var form = zone.closest("form");
        var submit = form ? form.querySelector("[data-cc-submit]") : null;

        function clearSelection() {
            input.value = "";
            zone.classList.remove("has-file");
            nameLabel.hidden = true;
            nameLabel.textContent = "";
        }

        function showSelection(file) {
            var error = validateFile(file);
            if (error) {
                clearSelection();
                window.CadToCode.showToast(error, 3200);
                return;
            }

            nameLabel.textContent = file.name + " (" + formatSize(file.size) + ")";
            nameLabel.hidden = false;
            zone.classList.add("has-file");
        }

        input.addEventListener("change", function () {
            if (input.files && input.files.length > 0) {
                showSelection(input.files[0]);
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

            try {
                var transfer = new DataTransfer();
                transfer.items.add(files[0]);
                input.files = transfer.files;
            } catch (e) {
                window.CadToCode.showToast("Drag and drop is not supported here. Please click to browse.", 3200);
                return;
            }

            showSelection(files[0]);
        });

        if (form) {
            form.addEventListener("submit", function (event) {
                if (!input.files || input.files.length === 0) {
                    event.preventDefault();
                    window.CadToCode.showToast("Please choose a file first.", 2600);
                    return;
                }

                var error = validateFile(input.files[0]);
                if (error) {
                    event.preventDefault();
                    window.CadToCode.showToast(error, 3200);
                    return;
                }

                if (submit) {
                    submit.disabled = true;
                    submit.textContent = "Analyzing...";
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