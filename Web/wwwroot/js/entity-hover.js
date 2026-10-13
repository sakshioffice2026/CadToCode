(function () {
    "use strict";

    var ACTIVE = "cc-entity-active";
    var current = null;

    function selectorFor(attribute, id) {
        return "[" + attribute + "=\"" + String(id).replace(/["\\]/g, "\\$&") + "\"]";
    }

    function findAll(selector) {
        return Array.prototype.slice.call(document.querySelectorAll(selector));
    }

    function clearActive() {
        findAll("." + ACTIVE).forEach(function (element) {
            element.classList.remove(ACTIVE);
        });
    }

    function scrollInside(container, element) {
        if (!container || !element) {
            return;
        }

        var box = container.getBoundingClientRect();
        var item = element.getBoundingClientRect();

        if (item.top < box.top) {
            container.scrollTop -= box.top - item.top + 8;
        } else if (item.bottom > box.bottom) {
            container.scrollTop += item.bottom - box.bottom + 8;
        }
    }

    function activate(id, source) {
        clearActive();

        if (!id) {
            return;
        }

        var targets = findAll(selectorFor("data-entity-id", id))
            .concat(findAll(selectorFor("data-profile-id", id)));

        var profileIds = [];

        targets.forEach(function (element) {
            element.classList.add(ACTIVE);

            var profile = element.getAttribute("data-profile-id");
            if (profile && profileIds.indexOf(profile) < 0) {
                profileIds.push(profile);
            }
        });

        profileIds.forEach(function (profile) {
            findAll(".cc-entity-code " + selectorFor("data-entity-id", profile)).forEach(function (element) {
                element.classList.add(ACTIVE);
            });
        });

        if (source && source.closest && source.closest("svg")) {
            var lines = findAll(".cc-entity-code .cc-entity-line" + selectorFor("data-entity-id", id));
            if (lines.length > 0) {
                scrollInside(lines[0].closest(".cc-code-body"), lines[0]);
            }
        }
    }

    function entityOf(target) {
        if (!target || !target.closest) {
            return null;
        }

        return target.closest("[data-entity-id]");
    }

    function handleEnter(event) {
        var element = entityOf(event.target);
        var id = element ? element.getAttribute("data-entity-id") : null;

        if (id === current) {
            return;
        }

        current = id;
        activate(id, element);
    }

    function handleLeave(event) {
        if (event.relatedTarget === null || event.relatedTarget === undefined) {
            current = null;
            clearActive();
        }
    }

    function init() {
        if (!document.querySelector("[data-entity-id]")) {
            return;
        }

        document.addEventListener("mouseover", handleEnter);
        document.addEventListener("mouseout", handleLeave);
        document.addEventListener("focusin", handleEnter);
        document.addEventListener("focusout", function () {
            current = null;
            clearActive();
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
