// Utility function to split text into spans
function splitTextToSpans(selector) {
    const elements = document.querySelectorAll(selector);
    elements.forEach((element) => {
        const text = element.textContent;
        element.innerHTML = ""; // Clear existing text
        text.split("").forEach((char) => {
            const span = document.createElement("span");
            span.textContent = char === " " ? "\u00A0" : char;
            element.appendChild(span);
        });
    });
}

// Utility function to play custom text animation using GSAP
function playAnimation(elementClass, animationName, callback) {
    const element = document.querySelector(elementClass);
    const chars = element ? element.querySelectorAll("span") : null;

    if (!chars) return;

    // Stop any ongoing animation on these characters
    gsap.killTweensOf(chars);

    // Reset styles to avoid residual effects
    gsap.set(chars, { opacity: 0, x: 0, y: 0, scale: 1, rotation: 0 });

    const { in: inAnimation, out: outAnimation } = animations[animationName];

    // Run "in" animation
    inAnimation(chars).then(() => {
        setTimeout(() => {
            // Run "out" animation
            outAnimation(chars).then(() => {
                // Animation cycle complete, invoke the callback
                if (callback && typeof callback === 'function') {
                    callback();
                }
            });
        }, 5000); // Delay between in and out animations
    });
}

// Function to animate text with a given animation
function animateTextContent(textSelector, textAnimationName, options = {}) {
    const { loop = false } = options; // Default to false if loop is not provided

    // Split the text into spans
    splitTextToSpans(textSelector);

    // Function to handle the animation
    function runAnimation() {
        playAnimation(textSelector, textAnimationName, () => {
            if (loop) {
                // If loop is true, run the animation again
                runAnimation();
            } else {
                console.log("Animation completed!");
            }
        });
    }

    // Start the animation
    runAnimation();
}



// Original createTicker function (refactored to use utility functions)
function createTicker({
    apiUrl,
    textSelector = '.animated-text',
    canvasSelector = '#animationCanvas',
    textAnimationName,
    dataSourceKey // Default key is 'Top', but can be customized
}) {
    const textWrapper = document.querySelector(textSelector);
    const canvas = document.querySelector(canvasSelector);
    let topData = [];
    let currentIndex = 0;
    let previousTopData = [];

    // Function to fetch data from API
    async function fetchTickerData() {
        try {
            const response = await fetch(apiUrl);
            const data = await response.json();
            const newTopData = data.map(item => item[dataSourceKey]); // Use dynamic key

            // Check if new data is different
            if (JSON.stringify(newTopData) !== JSON.stringify(previousTopData)) {
                previousTopData = newTopData;
                topData = newTopData;
                resetTicker();
                startTickerAnimation();
            }
        } catch (error) {
            textWrapper.textContent = "Error loading data";
            console.error(error);
        }
    }

    // Reset ticker animation
    function resetTicker() {
        currentIndex = 0;
        textWrapper.textContent = ""; // Clear current text
    }

    function startTickerAnimation() {
        if (!topData.length) return;

        function animateText() {
            // Update text and split into spans
            textWrapper.textContent = topData[currentIndex];
            splitTextToSpans(textSelector);

            // Play the custom text animation with a callback
            playAnimation(textSelector, textAnimationName, () => {
                // After text animation completes, move to the next text
                currentIndex = (currentIndex + 1) % topData.length;
                animateText(); // Recursively call animateText to continue the loop
            });
        }

        // Start the first animation
        animateText();
    }

    // Fetch data and start ticker
    setInterval(fetchTickerData, 5000); // Fetch data every 5 seconds
    fetchTickerData(); // Initial fetch
}

// Device pixel ratio adjustment
if (window.devicePixelRatio !== 1) {
    const scaleFactor = window.devicePixelRatio;
    document.body.style.transform = `scale(${1 / scaleFactor})`;
    document.body.style.transformOrigin = "0 0";
}