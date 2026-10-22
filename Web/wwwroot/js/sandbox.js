(function () {
    "use strict";

    /* ---------- Syntax highlighting ---------- */

    var KEYWORDS = toSet([
        "abstract", "as", "base", "break", "case", "catch", "class", "const", "continue",
        "default", "do", "else", "enum", "event", "explicit", "extern", "finally", "for",
        "foreach", "get", "if", "implicit", "in", "init", "interface", "internal", "is",
        "namespace", "new", "operator", "out", "override", "params", "partial", "private",
        "protected", "public", "readonly", "record", "ref", "required", "return", "sealed",
        "set", "static", "struct", "switch", "this", "throw", "try", "using", "virtual",
        "void", "while"
    ]);

    var TYPES = toSet([
        "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object",
        "sbyte", "short", "string", "uint", "ulong", "ushort", "var",
        "List", "Dictionary", "IEnumerable", "DateTime", "Guid",
        "StepDocument", "Solid", "Profile2D", "Math"
    ]);

    var LITERALS = toSet(["true", "false", "null"]);
    var DECLARATIONS = toSet(["class", "struct", "record", "enum", "interface", "new"]);

    var TOKEN_PATTERN = /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|("(?:[^"\\\n]|\\.)*")|(\b\d+(?:\.\d+)?[mMfFdDlLuU]?\b)|([A-Za-z_][A-Za-z0-9_]*)/g;

    function toSet(list) {
        var set = {};
        list.forEach(function (item) { set[item] = true; });
        return set;
    }

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

    function highlightInto(element, source) {
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

                if (LITERALS[word]) {
                    appendToken(fragment, word, "tok-bool");
                } else if (KEYWORDS[word]) {
                    appendToken(fragment, word, "tok-keyword");
                } else if (TYPES[word] || DECLARATIONS[previousWord]) {
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
        appendText(fragment, "\n");

        element.textContent = "";
        element.appendChild(fragment);
    }

    /* ---------- Helpers ---------- */

    function notify(message) {
        if (window.CadToCode && typeof window.CadToCode.showToast === "function") {
            window.CadToCode.showToast(message);
        }
    }

    function antiForgeryToken() {
        var input = document.querySelector("input[name='__RequestVerificationToken']");
        return input ? input.value : "";
    }

    function ensureStepName(name) {
        var fileName = (name || "model.step").trim();
        if (!/\.(step|stp)$/i.test(fileName)) {
            fileName += ".step";
        }
        return fileName;
    }

    function downloadStep(content, name) {
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

    /* ---------- 3D viewer ---------- */

    var Viewer = (function () {
        var PALETTE = [0x38bdf8, 0xff7a59, 0x34d399, 0xfbbf24, 0xa78bfa, 0xf472b6, 0x60a5fa, 0x2dd4bf];

        var host = null;
        var emptyNote = null;
        var statusChip = null;
        var ready = false;
        var failed = false;

        var renderer, scene, camera, group, grid, axes;
        var wireframe = false;
        var dirty = false;
        var target = null;
        var spherical = { radius: 100, theta: -Math.PI / 4, phi: Math.PI / 3 };
        var home = { radius: 100, theta: -Math.PI / 4, phi: Math.PI / 3 };
        var drag = null;

        function setStatus(text) {
            if (statusChip) {
                statusChip.textContent = text;
            }
        }

        function init() {
            if (ready || failed) {
                return ready;
            }

            host = document.querySelector("[data-cc-viewer]");
            if (!host) {
                failed = true;
                return false;
            }

            emptyNote = host.querySelector("[data-cc-viewer-empty]");
            statusChip = document.querySelector("[data-cc-viewer-status]");

            if (typeof window.THREE === "undefined") {
                failed = true;
                setStatus("3D library not loaded");
                return false;
            }

            var THREE = window.THREE;

            try {
                renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
            } catch (error) {
                failed = true;
                renderer = null;
                setStatus("WebGL not available");
                return false;
            }

            renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
            renderer.domElement.className = "cc-viewer-canvas";
            host.appendChild(renderer.domElement);

            scene = new THREE.Scene();
            camera = new THREE.PerspectiveCamera(45, 1, 0.1, 100000);
            camera.up.set(0, 0, 1);
            target = new THREE.Vector3(0, 0, 0);

            scene.add(new THREE.HemisphereLight(0xffffff, 0x223044, 0.85));
            var key = new THREE.DirectionalLight(0xffffff, 0.8);
            key.position.set(1, -1.4, 2);
            scene.add(key);
            var fill = new THREE.DirectionalLight(0xffffff, 0.35);
            fill.position.set(-1.5, 1, -1);
            scene.add(fill);

            group = new THREE.Group();
            scene.add(group);

            bindControls();
            bindToolbar();

            if (typeof ResizeObserver !== "undefined") {
                new ResizeObserver(resize).observe(host);
            } else {
                window.addEventListener("resize", resize);
            }

            ready = true;
            resize();
            return true;
        }

        function resize() {
            if (!ready) {
                return;
            }

            var width = Math.max(host.clientWidth, 1);
            var height = Math.max(host.clientHeight, 1);
            renderer.setSize(width, height, false);
            camera.aspect = width / height;
            camera.updateProjectionMatrix();
            requestRender();
        }

        function requestRender() {
            if (dirty) {
                return;
            }
            dirty = true;
            window.requestAnimationFrame(function () {
                dirty = false;
                updateCamera();
                renderer.render(scene, camera);
            });
        }

        function updateCamera() {
            var s = spherical;
            camera.position.set(
                target.x + s.radius * Math.sin(s.phi) * Math.cos(s.theta),
                target.y + s.radius * Math.sin(s.phi) * Math.sin(s.theta),
                target.z + s.radius * Math.cos(s.phi));
            camera.lookAt(target);
        }

        function bindControls() {
            var el = renderer.domElement;

            el.addEventListener("contextmenu", function (event) { event.preventDefault(); });

            el.addEventListener("pointerdown", function (event) {
                drag = {
                    x: event.clientX,
                    y: event.clientY,
                    pan: event.button === 2 || event.button === 1 || event.shiftKey
                };
                el.setPointerCapture(event.pointerId);
            });

            el.addEventListener("pointermove", function (event) {
                if (!drag) {
                    return;
                }

                var dx = event.clientX - drag.x;
                var dy = event.clientY - drag.y;
                drag.x = event.clientX;
                drag.y = event.clientY;

                if (drag.pan) {
                    var unit = (2 * spherical.radius * Math.tan((camera.fov * Math.PI) / 360)) / Math.max(host.clientHeight, 1);
                    camera.updateMatrix();
                    var right = new window.THREE.Vector3().setFromMatrixColumn(camera.matrix, 0);
                    var up = new window.THREE.Vector3().setFromMatrixColumn(camera.matrix, 1);
                    target.addScaledVector(right, -dx * unit);
                    target.addScaledVector(up, dy * unit);
                } else {
                    spherical.theta -= dx * 0.008;
                    spherical.phi = Math.min(Math.max(spherical.phi - dy * 0.008, 0.05), Math.PI - 0.05);
                }

                requestRender();
            });

            function endDrag(event) {
                drag = null;
                if (el.hasPointerCapture(event.pointerId)) {
                    el.releasePointerCapture(event.pointerId);
                }
            }

            el.addEventListener("pointerup", endDrag);
            el.addEventListener("pointercancel", endDrag);

            el.addEventListener("wheel", function (event) {
                event.preventDefault();
                spherical.radius = Math.max(spherical.radius * Math.exp(event.deltaY * 0.0012), 0.01);
                requestRender();
            }, { passive: false });
        }

        function bindToolbar() {
            var fit = document.querySelector("[data-cc-viewer-fit]");
            var wire = document.querySelector("[data-cc-viewer-wire]");

            if (fit) {
                fit.addEventListener("click", function () {
                    if (group && group.children.length > 0) {
                        frame(false);
                    }
                });
            }

            if (wire) {
                wire.addEventListener("click", function () {
                    wireframe = !wireframe;
                    wire.classList.toggle("is-active", wireframe);
                    applyWireframe();
                    requestRender();
                });
            }
        }

        function applyWireframe() {
            group.children.forEach(function (child) {
                if (child.isMesh) {
                    child.material.wireframe = wireframe;
                }
                if (child.isLineSegments) {
                    child.visible = !wireframe;
                }
            });
        }

        function clearGroup() {
            while (group.children.length > 0) {
                var child = group.children[0];
                group.remove(child);
                if (child.geometry) {
                    child.geometry.dispose();
                }
                if (child.material) {
                    child.material.dispose();
                }
            }

            [grid, axes].forEach(function (helper) {
                if (helper) {
                    scene.remove(helper);
                    helper.geometry.dispose();
                    helper.material.dispose();
                }
            });
            grid = null;
            axes = null;
        }

        function toVector(points) {
            return points.map(function (p) { return new window.THREE.Vector2(p.x, p.y); });
        }

        function buildMesh(solid, color) {
            var THREE = window.THREE;
            var geometry;

            if (solid.type === "prism" && solid.outer && solid.outer.length >= 3) {
                var shape = new THREE.Shape(toVector(solid.outer));
                (solid.holes || []).forEach(function (hole) {
                    if (hole.length >= 3) {
                        shape.holes.push(new THREE.Path(toVector(hole)));
                    }
                });
                geometry = new THREE.ExtrudeGeometry(shape, {
                    depth: Math.max(solid.depth || 0.001, 0.001),
                    bevelEnabled: false,
                    curveSegments: 24
                });
                geometry.translate(0, 0, solid.z || 0);
            } else if (solid.type === "cylinder") {
                var height = Math.max(solid.height || 0.001, 0.001);
                geometry = new THREE.CylinderGeometry(solid.radius, solid.radius, height, 64);
                geometry.rotateX(Math.PI / 2);
                geometry.translate(solid.x || 0, solid.y || 0, (solid.z || 0) + height / 2);
            } else {
                return null;
            }

            var material = new THREE.MeshPhongMaterial({
                color: color,
                shininess: 55,
                side: THREE.DoubleSide,
                wireframe: wireframe
            });

            var mesh = new THREE.Mesh(geometry, material);
            mesh.name = solid.name || solid.type;

            var edges = new THREE.LineSegments(
                new THREE.EdgesGeometry(geometry, 25),
                new THREE.LineBasicMaterial({ color: 0x0a0e18 }));
            edges.visible = !wireframe;

            return [mesh, edges];
        }

        function frame(rebuildHome) {
            var THREE = window.THREE;
            var box = new THREE.Box3().setFromObject(group);
            if (box.isEmpty()) {
                return;
            }

            var size = box.getSize(new THREE.Vector3());
            var center = box.getCenter(new THREE.Vector3());
            var span = Math.max(size.x, size.y, size.z, 0.001);

            target.copy(center);
            var radius = (span * 0.5) / Math.tan((camera.fov * Math.PI) / 360) * 1.9;

            if (rebuildHome) {
                home.radius = radius;
                camera.near = Math.max(span / 1000, 0.001);
                camera.far = span * 200;
                camera.updateProjectionMatrix();
            }

            spherical.radius = home.radius;
            spherical.theta = home.theta;
            spherical.phi = home.phi;
            requestRender();
        }

        function render(solids) {
            if (!init()) {
                return false;
            }

            var THREE = window.THREE;
            clearGroup();

            var count = 0;
            (solids || []).forEach(function (solid, index) {
                var built = buildMesh(solid, PALETTE[index % PALETTE.length]);
                if (built) {
                    group.add(built[0]);
                    group.add(built[1]);
                    count++;
                }
            });

            if (count === 0) {
                if (emptyNote) {
                    emptyNote.hidden = false;
                }
                setStatus("No solids to show");
                requestRender();
                return false;
            }

            if (emptyNote) {
                emptyNote.hidden = true;
            }

            var box = new THREE.Box3().setFromObject(group);
            var size = box.getSize(new THREE.Vector3());
            var span = Math.max(size.x, size.y, size.z, 1);

            grid = new THREE.GridHelper(span * 2.4, 24, 0x34425f, 0x1c2740);
            grid.rotateX(Math.PI / 2);
            grid.position.set(
                (box.min.x + box.max.x) / 2,
                (box.min.y + box.max.y) / 2,
                box.min.z - span * 0.002);
            scene.add(grid);

            axes = new THREE.AxesHelper(span * 0.35);
            axes.position.set(box.min.x, box.min.y, box.min.z);
            scene.add(axes);

            frame(true);
            setStatus(count + (count === 1 ? " solid" : " solids"));
            return true;
        }

        return { render: render, init: init };
    })();

    /* ---------- Sandbox panels ---------- */

    function initSandbox(root) {
        var source = document.querySelector(root.getAttribute("data-cc-source"));
        var runUrl = root.getAttribute("data-cc-run-url");
        var runButton = root.querySelector("[data-cc-run]");
        var downloadButton = root.querySelector("[data-cc-download]");
        var resetButton = root.querySelector("[data-cc-reset]");
        var consoleBox = root.querySelector("[data-cc-console]");
        var editor = root.querySelector("[data-cc-editor]");

        if (!source || !runUrl) {
            return;
        }

        var initialCode = source.value;
        var lastCode = null;
        var lastResult = null;
        var busy = false;

        if (editor) {
            initEditor(editor, source);
        }

        function setBusy(state) {
            busy = state;
            [runButton, downloadButton, resetButton].forEach(function (button) {
                if (button) {
                    button.disabled = state;
                }
            });
            if (runButton) {
                runButton.classList.toggle("is-busy", state);
            }
        }

        function showConsole(result) {
            if (!consoleBox) {
                return;
            }

            consoleBox.hidden = false;
            consoleBox.textContent = "";
            consoleBox.className = "cc-sandbox-console " + (result.success ? "is-ok" : "is-error");

            var head = document.createElement("div");
            head.className = "cc-console-head";
            head.textContent = (result.success ? "OK" : "FAILED")
                + (result.message ? " \u2014 " + result.message : "")
                + (typeof result.elapsedMs === "number" ? " (" + result.elapsedMs + " ms)" : "");
            consoleBox.appendChild(head);

            if (result.truncated) {
                appendLine(consoleBox, "cc-console-warn", "Output was truncated by the sandbox limits.");
            }

            (result.diagnostics || []).forEach(function (diagnostic) {
                var line = document.createElement("button");
                line.type = "button";
                line.className = "cc-console-diag is-" + (diagnostic.severity || "error");
                var where = diagnostic.line > 0 ? "(" + diagnostic.line + "," + diagnostic.column + ") " : "";
                line.textContent = where + (diagnostic.severity || "error") + ": " + diagnostic.message;
                line.addEventListener("click", function () {
                    jumpTo(diagnostic.line, diagnostic.column);
                });
                consoleBox.appendChild(line);
            });

            (result.output || []).forEach(function (text) {
                appendLine(consoleBox, "cc-console-out", text);
            });
        }

        function appendLine(parent, className, text) {
            var line = document.createElement("div");
            line.className = className;
            line.textContent = text;
            parent.appendChild(line);
        }

        function jumpTo(lineNumber, column) {
            if (!editor || !(lineNumber > 0)) {
                return;
            }

            var lines = source.value.split("\n");
            var index = 0;
            for (var i = 0; i < lineNumber - 1 && i < lines.length; i++) {
                index += lines[i].length + 1;
            }
            index += Math.max((column || 1) - 1, 0);

            source.focus();
            source.setSelectionRange(index, index);
            var lineHeight = parseFloat(window.getComputedStyle(source).lineHeight) || 20;
            source.scrollTop = Math.max((lineNumber - 3) * lineHeight, 0);
            source.dispatchEvent(new Event("scroll"));
        }

        function execute() {
            var code = source.value;

            return fetch(runUrl, {
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
                    lastCode = code;
                    lastResult = result;
                    return result;
                });
        }

        function run(forceDownload) {
            if (busy) {
                return;
            }

            setBusy(true);

            execute()
                .then(function (result) {
                    showConsole(result);

                    if (!result.success) {
                        notify("Run failed. See the console below the code.");
                        return;
                    }

                    try {
                        Viewer.render(result.solids);
                    } catch (viewerError) {
                        if (window.console && typeof window.console.error === "function") {
                            window.console.error("CadToCode 3D view failed", viewerError);
                        }
                        notify("3D view failed: " + (viewerError && viewerError.message ? viewerError.message : "unknown error"));
                    }

                    if (result.stepContent) {
                        downloadStep(result.stepContent, result.fileName);
                        notify("Downloaded " + ensureStepName(result.fileName));
                    }
                })
                .catch(function (error) {
                    showConsole({
                        success: false,
                        message: error && error.message ? error.message : "The request failed.",
                        diagnostics: [],
                        output: []
                    });
                    notify("Could not reach the server.");
                })
                .then(function () {
                    setBusy(false);
                });
        }

        function download() {
            if (busy) {
                return;
            }

            if (lastResult && lastResult.success && lastResult.stepContent && lastCode === source.value) {
                downloadStep(lastResult.stepContent, lastResult.fileName);
                notify("Downloaded " + ensureStepName(lastResult.fileName));
                return;
            }

            run(true);
        }

        if (runButton) {
            runButton.addEventListener("click", function () { run(true); });
        }

        if (downloadButton) {
            downloadButton.addEventListener("click", download);
        }

        if (resetButton) {
            resetButton.addEventListener("click", function () {
                source.value = initialCode;
                source.dispatchEvent(new Event("input"));
                notify("Script reset to the generated version.");
            });
        }
    }

    function initEditor(editor, textarea) {
        var code = editor.querySelector("code");
        var pre = editor.querySelector("pre");

        function refresh() {
            highlightInto(code, textarea.value);
        }

        function syncScroll() {
            pre.scrollTop = textarea.scrollTop;
            pre.scrollLeft = textarea.scrollLeft;
        }

        textarea.addEventListener("input", refresh);
        textarea.addEventListener("scroll", syncScroll);

        textarea.addEventListener("keydown", function (event) {
            if (event.key !== "Tab") {
                return;
            }

            event.preventDefault();
            var start = textarea.selectionStart;
            var end = textarea.selectionEnd;
            textarea.value = textarea.value.substring(0, start) + "    " + textarea.value.substring(end);
            textarea.selectionStart = textarea.selectionEnd = start + 4;
            refresh();
        });

        refresh();
    }

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("[data-cc-sandbox]").forEach(initSandbox);
        Viewer.init();
    });
})();
