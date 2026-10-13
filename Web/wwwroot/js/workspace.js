(function () {
    "use strict";

    var CLOSE_FALLBACK_MS = 220;

    var openers = {};
    var activeModal = null;
    var lastTrigger = null;

    function reducedMotion() {
        return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    }

    function setExpanded(id, expanded) {
        (openers[id] || []).forEach(function (button) {
            button.setAttribute("aria-expanded", expanded ? "true" : "false");
        });
    }

    function openModal(modal, trigger) {
        if (!modal || modal.open) {
            return;
        }

        if (activeModal && activeModal !== modal) {
            finishClose(activeModal, false);
        }

        lastTrigger = trigger || null;
        activeModal = modal;
        modal.classList.remove("is-closing");

        if (typeof modal.showModal === "function") {
            modal.showModal();
        } else {
            modal.setAttribute("open", "");
        }

        document.body.classList.add("cc-modal-open");
        setExpanded(modal.id, true);

        var body = modal.querySelector(".cc-modal-body");
        if (body) {
            body.scrollTop = 0;
        }

        window.requestAnimationFrame(function () {
            window.dispatchEvent(new Event("resize"));
        });
    }

    function finishClose(modal, restoreFocus) {
        modal.classList.remove("is-closing");

        if (typeof modal.close === "function") {
            if (modal.open) {
                modal.close();
            }
        } else {
            modal.removeAttribute("open");
        }

        setExpanded(modal.id, false);

        if (activeModal === modal) {
            activeModal = null;
            document.body.classList.remove("cc-modal-open");
        }

        if (restoreFocus && lastTrigger && typeof lastTrigger.focus === "function") {
            lastTrigger.focus();
        }
    }

    function closeModal(modal) {
        if (!modal || !modal.open || modal.classList.contains("is-closing")) {
            return;
        }

        if (reducedMotion()) {
            finishClose(modal, true);
            return;
        }

        var done = false;

        function complete() {
            if (done) {
                return;
            }
            done = true;
            modal.removeEventListener("animationend", onEnd);
            finishClose(modal, true);
        }

        function onEnd(event) {
            if (event.target === modal) {
                complete();
            }
        }

        modal.classList.add("is-closing");
        modal.addEventListener("animationend", onEnd);
        window.setTimeout(complete, CLOSE_FALLBACK_MS);
    }

    function initModal(modal) {
        modal.addEventListener("cancel", function (event) {
            event.preventDefault();
            closeModal(modal);
        });

        modal.addEventListener("click", function (event) {
            if (event.target === modal) {
                closeModal(modal);
            }
        });

        modal.addEventListener("close", function () {
            setExpanded(modal.id, false);
            if (activeModal === modal) {
                activeModal = null;
                document.body.classList.remove("cc-modal-open");
            }
        });

        modal.querySelectorAll("[data-cc-close]").forEach(function (button) {
            button.addEventListener("click", function () {
                closeModal(modal);
            });
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("dialog.cc-modal").forEach(initModal);

        document.querySelectorAll("[data-cc-open]").forEach(function (button) {
            var id = button.getAttribute("data-cc-open");
            button.setAttribute("aria-expanded", "false");

            if (!openers[id]) {
                openers[id] = [];
            }
            openers[id].push(button);

            button.addEventListener("click", function () {
                openModal(document.getElementById(id), button);
            });
        });

        document.addEventListener("keydown", function (event) {
            if (event.key === "Escape" && activeModal && typeof activeModal.showModal !== "function") {
                closeModal(activeModal);
            }
        });
    });

    window.CadToCodeWorkspace = {
        open: function (id) {
            openModal(document.getElementById(id), null);
        },
        close: function () {
            closeModal(activeModal);
        }
    };
})();
