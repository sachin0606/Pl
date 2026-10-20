function playImageSequenceAnimation(
    canvas, 
    imageFolder, 
    frameCount, 
    fps, 
    fileNameTemplate, 
    extension = '.png', 
    leadingZeros = 5, 
    startIndex = 0, 
    loop = true, 
    holdLastFrame = false, 
    onComplete = null
) {
    const ctx = canvas.getContext('2d');
    const images = [];
    let currentFrame = 0;
    let isPlaying = true;
    let imageLoadedCount = 0;

    // Load images
    for (let i = 0; i < frameCount; i++) {
        const img = new Image();
        const paddedIndex = (startIndex + i).toString().padStart(leadingZeros, '0');
        const fileName = fileNameTemplate.replace("{index}", paddedIndex);
        img.src = `${imageFolder}${fileName}${extension}`;

        img.onload = () => {
            imageLoadedCount++;
            if (imageLoadedCount === 1) {
                canvas.width = img.width;
                canvas.height = img.height;
            }
            if (imageLoadedCount === frameCount) {
                playImageFrames(); // Start playing when all images are loaded
            }
        };

        images.push(img);
    }

    // Play the animation frames
    function playImageFrames() {
        if (isPlaying) {
            ctx.clearRect(0, 0, canvas.width, canvas.height); // Clear canvas
            ctx.drawImage(images[currentFrame], 0, 0, canvas.width, canvas.height); // Draw the current frame
            currentFrame = (currentFrame + 1) % frameCount;

            // Handle end of animation
            if (!loop && currentFrame === 0) {
                isPlaying = false;

                if (holdLastFrame) {
                    ctx.drawImage(images[frameCount - 1], 0, 0, canvas.width, canvas.height); // Hold last frame
                } else {
                    ctx.clearRect(0, 0, canvas.width, canvas.height); // Clear canvas
                }

                // Callback
                if (onComplete && typeof onComplete === 'function') {
                    onComplete();
                }
                return;
            }

            setTimeout(playImageFrames, 1000 / fps); // Schedule the next frame
        }
    }

    // Toggle play/pause on click
    canvas.addEventListener('click', () => {
        isPlaying = !isPlaying;
        if (isPlaying) playImageFrames();
    });
}
