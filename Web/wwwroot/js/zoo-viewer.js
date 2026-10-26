(function () {
    "use strict";

    var OCCT_VERSION = "0.0.23";
    var OCCT_BASE = "https://cdn.jsdelivr.net/npm/occt-import-js@" + OCCT_VERSION + "/dist/";
    var PALETTE = [0x38bdf8, 0xff7a59, 0x34d399, 0xfbbf24, 0xa78bfa, 0xf472b6, 0x60a5fa, 0x2dd4bf];

    var host = document.querySelector("[data-zoo-viewer]");

    if (!host) {
        return;
    }

    var emptyNote = host.querySelector("[data-zoo-viewer-empty]");
    var statusChip = document.querySelector("[data-zoo-viewer-status]");
    var fitButton = document.querySelector("[data-zoo-viewer-fit]");
    var wireButton = document.querySelector("[data-zoo-viewer-wire]");
    var fileInput = document.querySelector("[data-zoo-viewer-file]");

    var ready = false;
    var failed = false;
    var wireframe = false;
    var dirty = false;
    var loadToken = 0;
    var occtPromise = null;

    var renderer, scene, camera, group, target;
    var spherical = { radius: 100, theta: -Math.PI / 4, phi: Math.PI / 3 };
    var home = { radius: 100, theta: -Math.PI / 4, phi: Math.PI / 3 };
    var drag = null;

    function setStatus(text) {
        if (statusChip) {
            statusChip.textContent = text;
        }
    }

    function setEmpty(title, body) {
        if (!emptyNote) {
            return;
        }

        emptyNote.hidden = !title;

        if (title) {
            emptyNote.textContent = "";

            var heading = document.createElement("p");
            heading.className = "cc-empty-title";
            heading.textContent = title;
            emptyNote.appendChild(heading);

            if (body) {
                var text = document.createElement("p");
                text.className = "mb-0";
                text.textContent = body;
                emptyNote.appendChild(text);
            }
        }
    }

    function init() {
        if (ready || failed) {
            return ready;
        }

        if (typeof window.THREE === "undefined") {
            failed = true;
            setStatus("3D library not loaded");
            setEmpty("3D library not loaded", "Reload the page and try again.");
            return false;
        }

        var THREE = window.THREE;

        try {
            renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        } catch (error) {
            failed = true;
            renderer = null;
            setStatus("WebGL not available");
            setEmpty("WebGL not available", "Enable hardware acceleration in your browser.");
            return false;
        }

        renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        renderer.domElement.className = "cc-zoo-viewer-canvas";
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
        if (dirty || !ready) {
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
        if (fitButton) {
            fitButton.addEventListener("click", function () {
                if (group && group.children.length > 0) {
                    frame(false);
                }
            });
        }

        if (wireButton) {
            wireButton.addEventListener("click", function () {
                wireframe = !wireframe;
                wireButton.classList.toggle("is-active", wireframe);
                applyWireframe();
                requestRender();
            });
        }

        if (fileInput) {
            fileInput.addEventListener("change", function () {
                var file = fileInput.files && fileInput.files[0];

                if (!file) {
                    return;
                }

                loadFile(file);
                fileInput.value = "";
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

    /* ---------- STEP loading ---------- */

    function loadScript(src) {
        return new Promise(function (resolve, reject) {
            var script = document.createElement("script");
            script.src = src;
            script.async = true;
            script.onload = function () { resolve(); };
            script.onerror = function () { reject(new Error("Could not load " + src)); };
            document.head.appendChild(script);
        });
    }

    function loadOcct() {
        if (occtPromise) {
            return occtPromise;
        }

        occtPromise = loadScript(OCCT_BASE + "occt-import-js.js")
            .then(function () {
                if (typeof window.occtimportjs !== "function") {
                    throw new Error("STEP reader unavailable");
                }

                return window.occtimportjs({
                    locateFile: function (name) { return OCCT_BASE + name; }
                });
            })
            .catch(function (error) {
                occtPromise = null;
                throw error;
            });

        return occtPromise;
    }

    function buildMeshes(result) {
        var THREE = window.THREE;
        var added = 0;

        (result.meshes || []).forEach(function (item, index) {
            var attributes = item.attributes || {};

            if (!attributes.position || !item.index) {
                return;
            }

            var geometry = new THREE.BufferGeometry();
            geometry.setAttribute("position", new THREE.Float32BufferAttribute(attributes.position.array, 3));

            if (attributes.normal) {
                geometry.setAttribute("normal", new THREE.Float32BufferAttribute(attributes.normal.array, 3));
            }

            geometry.setIndex(new THREE.Uint32BufferAttribute(item.index.array, 1));

            if (!attributes.normal) {
                geometry.computeVertexNormals();
            }

            var color = PALETTE[index % PALETTE.length];

            if (item.color && item.color.length >= 3) {
                color = new THREE.Color(item.color[0], item.color[1], item.color[2]);
            }

            var material = new THREE.MeshPhongMaterial({
                color: color,
                shininess: 55,
                side: THREE.DoubleSide,
                wireframe: wireframe
            });

            var mesh = new THREE.Mesh(geometry, material);
            mesh.name = item.name || "part-" + (index + 1);
            group.add(mesh);

            var edges = new THREE.LineSegments(
                new THREE.EdgesGeometry(geometry, 30),
                new THREE.LineBasicMaterial({ color: 0x0b1220 }));
            edges.visible = !wireframe;
            group.add(edges);

            added += 1;
        });

        return added;
    }

    function showBuffer(buffer, label) {
        var token = ++loadToken;

        if (!init()) {
            return Promise.resolve(false);
        }

        clearGroup();
        requestRender();
        setEmpty("Loading model", "Reading " + label + "...");
        setStatus("Loading...");

        return loadOcct()
            .then(function (occt) {
                if (token !== loadToken) {
                    return false;
                }

                var result = occt.ReadStepFile(new Uint8Array(buffer), null);

                if (!result || !result.success) {
                    throw new Error("The STEP file could not be read");
                }

                clearGroup();

                var count = buildMeshes(result);

                if (count === 0) {
                    throw new Error("The STEP file has no solids to display");
                }

                setEmpty("");
                setStatus(label + " - " + count + (count === 1 ? " part" : " parts"));
                frame(true);
                return true;
            })
            .catch(function (error) {
                if (token !== loadToken) {
                    return false;
                }

                clearGroup();
                requestRender();
                setStatus("Preview failed");
                setEmpty("Preview failed", error && error.message ? error.message : "The model could not be shown.");
                return false;
            });
    }

    function loadUrl(url, label) {
        var token = loadToken + 1;

        if (!init()) {
            return Promise.resolve(false);
        }

        setEmpty("Loading model", "Fetching " + label + "...");
        setStatus("Loading...");

        return fetch(url, { credentials: "same-origin" })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("The file could not be downloaded");
                }

                return response.arrayBuffer();
            })
            .then(function (buffer) {
                return showBuffer(buffer, label);
            })
            .catch(function (error) {
                if (token < loadToken) {
                    return false;
                }

                setStatus("Preview failed");
                setEmpty("Preview failed", error && error.message ? error.message : "The model could not be shown.");
                return false;
            });
    }

    function loadFile(file) {
        return new Promise(function (resolve) {
            var reader = new FileReader();

            reader.onload = function () {
                resolve(showBuffer(reader.result, file.name));
            };

            reader.onerror = function () {
                setStatus("Preview failed");
                setEmpty("Preview failed", "The file could not be read.");
                resolve(false);
            };

            reader.readAsArrayBuffer(file);
        });
    }

    function reset() {
        loadToken += 1;

        if (ready) {
            clearGroup();
            requestRender();
        }

        setStatus("No model loaded");
        setEmpty("No 3D model yet", "Generate a STEP model or open a .step file to see it here.");
    }

    function isStepName(name) {
        return /\.(step|stp)$/i.test(name || "");
    }

    window.CcZooViewer = {
        loadUrl: loadUrl,
        loadFile: loadFile,
        reset: reset,
        isStepName: isStepName
    };
}());
