namespace JarvisCode.App.Services;

/// <summary>
/// The Browser pane's in-page scripts, extracted VERBATIM from the installed
/// Claude Code Desktop's app.asar (1.40609.0.0, index.chunk-C5__TEgr.js) — the
/// a11y-tree generator (their kOn content script: __claudeElementMap string
/// refs, sensitive-value redaction, YAML built in the page), the ref-to-point
/// resolver, the form_input setter and the get_page_text extractor. Everything
/// runs in the MAIN frame only, exactly like the reference: iframe content is
/// never traversed, refs never exist inside iframes, and embedded content is
/// reached by screenshot coordinates. Placeholders (__REF__ etc.) are replaced
/// with JSON-serialized values by the caller.
/// </summary>
internal static class BrowserPaneScripts
{
    /// <summary>The reference's accessibility-tree content script, verbatim.</summary>
    public const string A11yTree = """
// Content script that defines the accessibility tree generation function in the MAIN context

(function () {
  // Initialize global element map and ref counter if not already present
  if (!window.__claudeElementMap) {
    window.__claudeElementMap = {};
  }
  // O(1) Element → ref lookup. Avoids the previous O(n²) linear scan of
  // __claudeElementMap on every included element. WeakMap so GC'd elements
  // drop out automatically. Initialised independently so a page that already
  // has __claudeElementMap from a previous injection still gets the index.
  if (!window.__claudeElementReverseMap) {
    window.__claudeElementReverseMap = new WeakMap();
  }
  if (!window.__claudeRefCounter) {
    window.__claudeRefCounter = 0;
  }

  // Define the accessibility tree generation function on the window (in content script context)
  window.__generateAccessibilityTree = function (
    filterType,
    maxDepth,
    maxChars,
    refId,
  ) {
    try {
      var result = [];
      var effectiveMaxDepth =
        maxDepth !== undefined && maxDepth !== null ? maxDepth : 15;

      function getRole(element) {
        var role = element.getAttribute("role");
        if (role) return role;

        var tag = element.tagName.toLowerCase();
        var type = element.getAttribute("type");

        var roleMap = {
          a: "link",
          button: "button",
          input:
            type === "submit" || type === "button"
              ? "button"
              : type === "checkbox"
                ? "checkbox"
                : type === "radio"
                  ? "radio"
                  : type === "file"
                    ? "button"
                    : "textbox",
          select: "combobox",
          textarea: "textbox",
          h1: "heading",
          h2: "heading",
          h3: "heading",
          h4: "heading",
          h5: "heading",
          h6: "heading",
          img: "image",
          nav: "navigation",
          main: "main",
          header: "banner",
          footer: "contentinfo",
          section: "region",
          article: "article",
          aside: "complementary",
          form: "form",
          table: "table",
          ul: "list",
          ol: "list",
          li: "listitem",
          label: "label",
        };

        return roleMap[tag] || "generic";
      }

      // password / hidden / OTP / credit-card field values must never be
      // serialized into the tree — find/read_page send the tree to the model.
      function isSensitiveInput(element) {
        var type = (element.getAttribute("type") || "").toLowerCase();
        if (type === "password" || type === "hidden") return true;

        var autocomplete = (
          element.getAttribute("autocomplete") || ""
        ).toLowerCase();
        var sensitiveAutocomplete = [
          "current-password",
          "new-password",
          "one-time-code",
          "cc-number",
          "cc-csc",
          "cc-exp",
          "cc-exp-month",
          "cc-exp-year",
        ];
        for (var i = 0; i < sensitiveAutocomplete.length; i++) {
          if (autocomplete.indexOf(sensitiveAutocomplete[i]) !== -1)
            return true;
        }
        return false;
      }

      // Direct text-node children only — used for label[for] resolution so a
      // wrapping <label> doesn't pull in nested <option>/<textarea> text.
      function directTextOf(el) {
        var t = "";
        for (var i = 0; i < el.childNodes.length; i++) {
          if (el.childNodes[i].nodeType === Node.TEXT_NODE)
            t += el.childNodes[i].textContent;
        }
        return t.trim();
      }

      function getCleanName(element) {
        var tag = element.tagName.toLowerCase();

        // For selects, get the selected option text
        if (tag === "select") {
          if (isSensitiveInput(element)) {
            // Preserve identifying labels (parity with the input flow below);
            // only redact the selected value.
            var selAria = element.getAttribute("aria-label");
            if (selAria && selAria.trim()) return selAria.trim();
            var selTitle = element.getAttribute("title");
            if (selTitle && selTitle.trim()) return selTitle.trim();
            if (element.id) {
              var selLabel = document.querySelector(
                'label[for="' + element.id + '"]',
              );
              if (selLabel) {
                var selLabelText = directTextOf(selLabel);
                if (selLabelText) return selLabelText;
              }
            }
            return "[value redacted]";
          }
          var selectElement = element;
          var selectedOption =
            selectElement.querySelector("option[selected]") ||
            selectElement.options[selectElement.selectedIndex];
          if (selectedOption && selectedOption.textContent) {
            return selectedOption.textContent.trim();
          }
        }

        // Priority order for getting meaningful names
        var ariaLabel = element.getAttribute("aria-label");
        if (ariaLabel && ariaLabel.trim()) return ariaLabel.trim();

        var placeholder = element.getAttribute("placeholder");
        if (placeholder && placeholder.trim()) return placeholder.trim();

        var title = element.getAttribute("title");
        if (title && title.trim()) return title.trim();

        var alt = element.getAttribute("alt");
        if (alt && alt.trim()) return alt.trim();

        // For form labels
        if (element.id) {
          var label = document.querySelector('label[for="' + element.id + '"]');
          if (label) {
            var labelText = directTextOf(label);
            if (labelText) return labelText;
          }
        }

        // For inputs with values
        if (tag === "input") {
          var inputElement = element;
          var type = element.getAttribute("type") || "";
          var value = element.getAttribute("value");

          if (type === "submit" && value && value.trim()) {
            return value.trim();
          }

          if (isSensitiveInput(element)) {
            return inputElement.value ? "[value redacted]" : "";
          }

          if (
            inputElement.value &&
            inputElement.value.length < 50 &&
            inputElement.value.trim()
          ) {
            return inputElement.value.trim();
          }
        }

        if (tag === "textarea" && isSensitiveInput(element)) {
          return element.value ? "[value redacted]" : "";
        }

        // For buttons, links, and other interactive elements, get direct text
        if (["button", "a", "summary"].includes(tag)) {
          var directText = "";
          for (var i = 0; i < element.childNodes.length; i++) {
            var node = element.childNodes[i];
            if (node.nodeType === Node.TEXT_NODE) {
              directText += node.textContent;
            }
          }
          if (directText.trim()) return directText.trim();
        }

        // For headings, get text content but limit it
        if (tag.match(/^h[1-6]$/)) {
          var headingText = element.textContent;
          if (headingText && headingText.trim()) {
            return headingText.trim().substring(0, 100);
          }
        }

        // ignore images without an "alt"
        if (tag === "img") {
          return "";
        }

        // For generic elements, get direct text content (not including child elements)
        // This helps capture important text in spans, divs, etc.
        var directTextContent = "";
        for (var j = 0; j < element.childNodes.length; j++) {
          var childNode = element.childNodes[j];
          if (childNode.nodeType === Node.TEXT_NODE) {
            directTextContent += childNode.textContent;
          }
        }

        if (
          directTextContent &&
          directTextContent.trim() &&
          directTextContent.trim().length >= 3
        ) {
          // Only return if it's meaningful text (at least 3 characters)
          var trimmedText = directTextContent.trim();
          if (trimmedText.length > 100) {
            return trimmedText.substring(0, 100) + "...";
          }
          return trimmedText;
        }

        return "";
      }

      function isVisible(element) {
        var style = window.getComputedStyle(element);
        return (
          style.display !== "none" &&
          style.visibility !== "hidden" &&
          style.opacity !== "0" &&
          element.offsetWidth > 0 &&
          element.offsetHeight > 0
        );
      }

      function isInteractive(element) {
        var tag = element.tagName.toLowerCase();
        var interactiveTags = [
          "a",
          "button",
          "input",
          "select",
          "textarea",
          "details",
          "summary",
        ];

        return (
          interactiveTags.includes(tag) ||
          element.getAttribute("onclick") !== null ||
          element.getAttribute("tabindex") !== null ||
          element.getAttribute("role") === "button" ||
          element.getAttribute("role") === "link" ||
          element.getAttribute("contenteditable") === "true"
        );
      }

      function isSemantic(element) {
        var tag = element.tagName.toLowerCase();
        var semanticTags = [
          "h1",
          "h2",
          "h3",
          "h4",
          "h5",
          "h6",
          "nav",
          "main",
          "header",
          "footer",
          "section",
          "article",
          "aside",
        ];
        return (
          semanticTags.includes(tag) || element.getAttribute("role") !== null
        );
      }

      function shouldIncludeElement(element, options) {
        var tag = element.tagName.toLowerCase();

        // Always skip these
        if (
          ["script", "style", "meta", "link", "title", "noscript"].includes(tag)
        )
          return false;
        if (
          options.filter !== "all" &&
          element.getAttribute("aria-hidden") === "true"
        )
          return false;

        // Check visibility unless using 'all' filter (which includes non-visible elements)
        if (options.filter !== "all" && !isVisible(element)) return false;

        // Skip viewport visibility check when refId is specified (we want all children of the ref element)
        // or when using 'all' filter
        if (options.filter !== "all" && !options.refId) {
          var rect = element.getBoundingClientRect();
          var inViewport =
            rect.top < window.innerHeight &&
            rect.bottom > 0 &&
            rect.left < window.innerWidth &&
            rect.right > 0;
          if (!inViewport) return false;
        }

        // Apply interactive filter if specified
        if (options.filter === "interactive") {
          return isInteractive(element);
        }

        // Default behavior when no filter is specified (all visible elements)
        // Always include interactive elements
        if (isInteractive(element)) return true;

        // Always include semantic elements (headings, nav, etc.)
        if (isSemantic(element)) return true;

        // Include elements with meaningful text content
        if (getCleanName(element).length > 0) return true;

        var elementRole = getRole(element);
        if (
          elementRole !== null &&
          elementRole !== "generic" &&
          elementRole !== "image"
        ) {
          return true;
        }

        return false;
      }

      // Hard cap on included elements per walk. Depth is already capped, but
      // a wide flat DOM (infinite-scroll feeds, huge tables) can still pin
      // the main thread past the 45s executeScript race. 10k is well above
      // typical pages and below the point where serialization alone is slow.
      var MAX_INCLUDED_NODES = 10000;
      var includedNodeCount = 0;

      function processElement(element, depth, options) {
        if (includedNodeCount >= MAX_INCLUDED_NODES) return;
        if (depth > effectiveMaxDepth) return; // Use configurable depth limit
        if (!element || !element.tagName) return;

        var shouldInclude =
          shouldIncludeElement(element, options) ||
          (options.refId !== null && depth === 0);

        if (shouldInclude) {
          var role = getRole(element);
          var name = getCleanName(element);

          var ref = window.__claudeElementReverseMap.get(element) || null;
          // The reverse map is weak, but the forward map's WeakRef may have
          // been swept while a stale reverse entry survived (different GC
          // timing). Verify the forward entry still points at this element.
          if (ref) {
            var fwd = window.__claudeElementMap[ref];
            if (!fwd || fwd.deref() !== element) ref = null;
          }

          // If not found, create a new ref
          if (!ref) {
            ref = "ref_" + ++window.__claudeRefCounter;
            window.__claudeElementMap[ref] = new WeakRef(element);
            window.__claudeElementReverseMap.set(element, ref);
          }
          includedNodeCount++;

          var yaml = " ".repeat(depth) + role;

          if (name) {
            // Clean up the name - remove newlines, limit length
            name = name.replace(/\s+/g, " ").substring(0, 100);
            yaml += ' "' + name.replace(/"/g, '\\"') + '"';
          }

          yaml += " [" + ref + "]";

          // Add useful attributes
          if (element.getAttribute("href"))
            yaml += ' href="' + element.getAttribute("href") + '"';
          if (element.getAttribute("type"))
            yaml += ' type="' + element.getAttribute("type") + '"';
          if (element.getAttribute("placeholder"))
            yaml +=
              ' placeholder="' + element.getAttribute("placeholder") + '"';

          result.push(yaml);

          // For select elements, add options as children
          var tag = element.tagName.toLowerCase();
          if (tag === "select" && !isSensitiveInput(element)) {
            var selectElement = element;
            var selectOptions = selectElement.options;
            for (var optIdx = 0; optIdx < selectOptions.length; optIdx++) {
              var opt = selectOptions[optIdx];
              var optYaml = " ".repeat(depth + 1) + "option";
              var optText = opt.textContent ? opt.textContent.trim() : "";
              if (optText) {
                optText = optText.replace(/\s+/g, " ").substring(0, 100);
                optYaml += ' "' + optText.replace(/"/g, '\\"') + '"';
              }
              // Mark selected option
              if (opt.selected) {
                optYaml += " (selected)";
              }
              // Add value if different from text
              if (opt.value && opt.value !== optText) {
                optYaml += ' value="' + opt.value.replace(/"/g, '\\"') + '"';
              }
              result.push(optYaml);
            }
          }
        }

        // Don't recurse into a sensitive <select> — option text would leak via
        // the generic child path even though the option-loop above is gated.
        var elTag = element.tagName.toLowerCase();
        if (elTag === "select" && isSensitiveInput(element)) return;

        // Always traverse children - we need to go deep to find interactive elements
        if (element.children && depth < effectiveMaxDepth) {
          for (var i = 0; i < element.children.length; i++) {
            processElement(
              element.children[i],
              shouldInclude ? depth + 1 : depth,
              options,
            );
          }
        }
      }

      var options = {
        filter: filterType || "all", // Default to "all" if no filter specified
        refId: refId,
      };

      // If refId is specified, find that element and process it
      if (refId) {
        var weakRef = window.__claudeElementMap[refId];
        if (!weakRef) {
          return {
            error:
              "Element with ref_id '" +
              refId +
              "' not found. It may have been removed from the page. Use read_page without ref_id to get the current page state.",
            pageContent: "",
            viewport: {
              width: window.innerWidth,
              height: window.innerHeight,
            },
          };
        }

        var targetElement = weakRef.deref();
        if (!targetElement) {
          return {
            error:
              "Element with ref_id '" +
              refId +
              "' no longer exists. It may have been removed from the page. Use read_page without ref_id to get the current page state.",
            pageContent: "",
            viewport: {
              width: window.innerWidth,
              height: window.innerHeight,
            },
          };
        }

        processElement(targetElement, 0, options);
      } else if (document.body) {
        processElement(document.body, 0, options);
      }

      // Clean up stale references (elements that have been garbage collected)
      for (var ref in window.__claudeElementMap) {
        var elementWeakRef = window.__claudeElementMap[ref];
        if (!elementWeakRef.deref()) {
          delete window.__claudeElementMap[ref];
        }
      }

      var pageContent = result.join("\n");

      if (includedNodeCount >= MAX_INCLUDED_NODES) {
        var truncHint = refId
          ? "use a smaller depth or focus on a more specific child element"
          : "use a refId or smaller depth to focus";
        pageContent +=
          "\n[truncated at " +
          MAX_INCLUDED_NODES +
          " elements — page is very large; " +
          truncHint +
          "]";
      }

      // Character count limit (skip if maxCharacters is null/undefined).
      // Truncate rather than error — pageContent is fully built and already
      // redacted at this point, so returning a prefix is strictly more useful
      // than discarding it. Cut at a newline boundary: the output is
      // line-oriented, and a mid-line cut would leave a dangling partial node.
      if (maxChars != null && pageContent.length > maxChars) {
        var fullLength = pageContent.length;
        var cutAt = pageContent.lastIndexOf("\n", maxChars);
        if (cutAt <= 0) {
          // No newline at or before maxChars. Clamp so a nonsensical
          // negative maxChars can't become a negative slice end, which would
          // mean "all but the last N characters" and return nearly everything.
          cutAt = Math.max(0, maxChars);
        }
        var focusHint = refId
          ? "use a smaller depth or focus on a more specific child element"
          : "use ref_id or a smaller depth to focus";
        pageContent =
          pageContent.slice(0, cutAt) +
          "\n[output truncated at " +
          maxChars +
          " of " +
          fullLength +
          " characters. Pass a larger max_chars (default 50000) to see more, or " +
          focusHint +
          ".]";
      }

      return {
        pageContent: pageContent,
        viewport: {
          width: window.innerWidth,
          height: window.innerHeight,
        },
      };
    } catch (error) {
      console.error("Error in accessibility tree generation:", error);
      throw new Error(
        "Error generating accessibility tree: " +
          (error.message || "Unknown error"),
      );
    }
  };
})();
""";

    /// <summary>
    /// read_page/find: the content script plus one call, in a single evaluation —
    /// the reference composes it the same way (kOn + ";window.__generateAccessibilityTree(...)").
    /// </summary>
    public static string A11yCall(string filterJson, string depthJson, string maxCharsJson, string refIdJson) =>
        A11yTree + $";window.__generateAccessibilityTree({filterJson}, {depthJson}, {maxCharsJson}, {refIdJson})";

    /// <summary>
    /// Resolves a ref to its viewport rect + layout-viewport size (the
    /// reference's g2n page half: scrollIntoView, rect center, and the
    /// scrollbar-aware layout viewport). __REF__ is the JSON ref string.
    /// </summary>
    public const string RefPoint = """
(function() {
    var map = window.__claudeElementMap;
    if (!map) return { error: "ref map not initialized; call read_page first" };
    var entry = map[__REF__];
    if (!entry) return { error: "ref not found: " + __REF__ };
    var el = typeof entry.deref === "function" ? entry.deref() : entry;
    if (!el || !el.isConnected) return { error: "ref is stale (element removed): " + __REF__ };
    el.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
    var r = el.getBoundingClientRect();
    // Layout viewport, not window.innerWidth/Height: with classic
    // (layout-consuming) scrollbars (Windows/Linux Chromium), innerWidth
    // includes the ~15-17px scrollbar gutter where no page content is
    // hit-testable — a click clamped to innerWidth-1 lands on the
    // scrollbar track and silently no-ops.
    var se =
      document.scrollingElement ||
      (document.compatMode === "BackCompat"
        ? document.body
        : document.documentElement) ||
      document.documentElement;
    var vw = Math.min(window.innerWidth, se.clientWidth || window.innerWidth);
    var vh = Math.min(window.innerHeight, se.clientHeight || window.innerHeight);
    return { x: r.x + r.width / 2, y: r.y + r.height / 2,
             left: r.left, top: r.top, right: r.right, bottom: r.bottom,
             vw: vw, vh: vh };
  })()
""";

    /// <summary>
    /// form_input's page half (the reference's h2n script): native setters so
    /// framework listeners fire, select by value-or-text, radio/checkbox
    /// handling. __REF__/__VALUE__/__CHECKED__ are JSON-serialized.
    /// </summary>
    public const string FormInput = """
(function() {
    var map = window.__claudeElementMap;
    if (!map) return { ok: false, ref: true, error: "ref map not initialized; call read_page first" };
    var entry = map[__REF__];
    var el = entry && typeof entry.deref === "function" ? entry.deref() : entry;
    if (!el || !el.isConnected) return { ok: false, ref: true, error: "ref not found or stale: " + __REF__ };
    el.focus();
    var tag = el.tagName.toLowerCase();
    var v = __VALUE__;
    function nativeSet(proto, prop, target, value) {
      var d = Object.getOwnPropertyDescriptor(proto, prop);
      if (d && d.set) { d.set.call(target, value); } else { target[prop] = value; }
    }
    if (tag === "select") {
      var opt = Array.from(el.options).find(function(o) { return o.value === v || o.text === v; });
      if (!opt) return { ok: false, error: "option not found" };
      nativeSet(window.HTMLSelectElement.prototype, "value", el, opt.value);
    } else if (el.type === "radio") {
      nativeSet(window.HTMLInputElement.prototype, "checked", el, true);
    } else if (el.type === "checkbox") {
      nativeSet(window.HTMLInputElement.prototype, "checked", el, __CHECKED__);
    } else if (tag === "input" || tag === "textarea") {
      var proto = tag === "textarea" ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
      nativeSet(proto, "value", el, v);
    } else if (el.isContentEditable) {
      el.textContent = v;
    } else {
      return { ok: false, error: "element is not fillable" };
    }
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    return { ok: true };
  })()
""";

    /// <summary>
    /// get_page_text's page half (the reference's m2n script): waits out a
    /// loading document, picks the densest content region, collapses blank
    /// runs. __MAXCHARS__ is a JSON number.
    /// </summary>
    public const string PageText = """
(async function() {
    if (!document.body) {
      if (document.readyState === "loading") {
        await new Promise(function (resolve) {
          document.addEventListener("DOMContentLoaded", resolve, { once: true });
          setTimeout(resolve, 2000);
        });
      }
      if (!document.body) return { loading: document.readyState === "loading" };
    }
    var selectors = ["article", "main", '[class*="articleBody"]', '[role="main"]', "#content"];
    var best = document.body, bestLen = 0;
    for (var i = 0; i < selectors.length; i++) {
      var el = document.querySelector(selectors[i]);
      if (el && el.innerText && el.innerText.length > bestLen) {
        best = el; bestLen = el.innerText.length;
      }
    }
    var text = (best.innerText || "").replace(/\n{3,}/g, "\n\n").trim();
    return {
      title: document.title,
      url: location.href,
      tag: best.tagName.toLowerCase(),
      text: text.slice(0, __MAXCHARS__),
      truncated: text.length > __MAXCHARS__
    };
  })()
""";
}
