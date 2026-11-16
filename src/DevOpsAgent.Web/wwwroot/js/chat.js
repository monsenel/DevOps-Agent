(() => {
    // Agent chat. Enter sends the message and Shift+Enter starts a new line; the conversation box scrolls inside itself and
    // follows the newest message unless the reader has scrolled up. The listeners are delegated, so they keep working while
    // Blazor re-renders the page.
    document.addEventListener("keydown", event => {
        const field = event.target;
        if (!(field instanceof HTMLTextAreaElement) || field.id !== "chat-prompt") return;
        // Not while an input method is composing a character (Persian, Chinese, Japanese ...): Enter confirms it there.
        if (event.key !== "Enter" || event.shiftKey || event.isComposing || event.keyCode === 229) return;
        event.preventDefault();
        field.form?.requestSubmit();
    });

    const nearBottom = box => box.scrollHeight - box.scrollTop - box.clientHeight < 48;

    // scroll events do not bubble: capture them to remember whether the reader is at the end of the conversation.
    document.addEventListener("scroll", event => {
        const box = event.target;
        if (box instanceof HTMLElement && box.classList.contains("chat-messages")) box.dataset.follow = nearBottom(box) ? "1" : "0";
    }, true);

    new MutationObserver(mutations => {
        const boxes = new Set();
        for (const mutation of mutations) {
            const node = mutation.target instanceof HTMLElement ? mutation.target : mutation.target.parentElement;
            const box = node?.closest(".chat-messages");
            if (box) boxes.add(box);
        }

        for (const box of boxes) if (box.dataset.follow !== "0") box.scrollTop = box.scrollHeight;
    }).observe(document.body, { childList: true, subtree: true, characterData: true });
})();
