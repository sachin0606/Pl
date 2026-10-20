window.onload = () => {
    // 1. Animate Side Cards coming in from sides
    gsap.from(".left .card", {
        x: -400,
        opacity: 0,
        duration: 1,
        stagger: 0.2,
        ease: "power4.out"
    });

    gsap.from(".right .card", {
        x: 400,
        opacity: 0,
        duration: 1,
        stagger: 0.2,
        ease: "power4.out"
    });

    // 2. Animate the central clock scaling up
    gsap.from(".clock-face", {
        scale: 0,
        rotation: 360,
        duration: 1.5,
        ease: "back.out(1.7)"
    });

    // 3. Make clock hands move smoothly
    function moveClock() {
        gsap.to(".hour", { rotation: "+=30", duration: 1, ease: "none" });
        gsap.to(".minute", { rotation: "+=360", duration: 10, repeat: -1, ease: "none" });
    }
    moveClock();

    // 4. Subtle pulse for the "Flash News" label
    gsap.to(".flash-label", {
        backgroundColor: "#ff0000",
        repeat: -1,
        yoyo: true,
        duration: 0.5
    });
};