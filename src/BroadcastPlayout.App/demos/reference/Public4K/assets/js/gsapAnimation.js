const animations = {
  "animation-1": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: 50 },
        { opacity: 1, y: 0, stagger: 0.05, duration: 1, ease: "power2.out" }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: -50,
        stagger: 0.05,
        duration: 1,
        ease: "power2.in",
      }),
  },
  "animation-2": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, x: 100 },
        { opacity: 1, x: 0, stagger: 0.05, duration: 1, ease: "power2.out" }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        x: -100,
        stagger: 0.05,
        duration: 1,
        ease: "power2.in",
      }),
  },
  "animation-3": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, scale: 0 },
        {
          opacity: 1,
          scale: 1,
          stagger: 0.05,
          duration: 1,
          ease: "back.out(1.7)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        scale: 0,
        stagger: 0.05,
        duration: 1,
        ease: "back.in(1.7)",
      }),
  },
  "animation-4": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotation: -90 },
        {
          opacity: 1,
          rotation: 0,
          stagger: 0.05,
          duration: 1,
          ease: "power2.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotation: 90,
        stagger: 0.05,
        duration: 1,
        ease: "power2.in",
      }),
  },
  "animation-5": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: -100 },
        {
          opacity: 1,
          y: 0,
          stagger: 0.05,
          duration: 1.5,
          ease: "elastic.out(1, 0.3)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: 100,
        stagger: 0.05,
        duration: 1.5,
        ease: "elastic.in(1, 0.3)",
      }),
  },
  "animation-6": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotationX: -90 },
        {
          opacity: 1,
          rotationX: 0,
          stagger: 0.05,
          duration: 1,
          ease: "power2.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotationX: 90,
        stagger: 0.05,
        duration: 1,
        ease: "power2.in",
      }),
  },
  "animation-7": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, scale: 0.5 },
        {
          opacity: 1,
          scale: 1,
          stagger: 0.05,
          duration: 1,
          ease: "bounce.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        scale: 0.5,
        stagger: 0.05,
        duration: 1,
        ease: "bounce.in",
      }),
  },
  "animation-8": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotationZ: -180 },
        {
          opacity: 1,
          rotationZ: 0,
          stagger: 0.05,
          duration: 1,
          ease: "power3.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotationZ: 180,
        stagger: 0.05,
        duration: 1,
        ease: "power3.in",
      }),
  },
  "animation-9": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, skewX: -45 },
        {
          opacity: 1,
          skewX: 0,
          stagger: 0.05,
          duration: 1,
          ease: "power4.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        skewX: 45,
        stagger: 0.05,
        duration: 1,
        ease: "power4.in",
      }),
  },
  "animation-10": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, skewY: 45 },
        {
          opacity: 1,
          skewY: 0,
          stagger: 0.05,
          duration: 1,
          ease: "power4.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        skewY: -45,
        stagger: 0.05,
        duration: 1,
        ease: "power4.in",
      }),
  },
  "animation-11": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: 200, scale: 0.5 },
        {
          opacity: 1,
          y: 0,
          scale: 1,
          stagger: 0.05,
          duration: 1,
          ease: "bounce.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: -200,
        scale: 0.5,
        stagger: 0.05,
        duration: 1,
        ease: "bounce.in",
      }),
  },
  "animation-12": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, x: -300 },
        {
          opacity: 1,
          x: 0,
          stagger: 0.05,
          duration: 1.5,
          ease: "elastic.out(1, 0.3)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        x: 300,
        stagger: 0.05,
        duration: 1.5,
        ease: "elastic.in(1, 0.3)",
      }),
  },
  "animation-13": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: 200, scale: 0.5 },
        {
          opacity: 1,
          y: 0,
          scale: 1,
          stagger: 0.05,
          duration: 1,
          ease: "back.out(2)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: -200,
        scale: 0.5,
        stagger: 0.05,
        duration: 1,
        ease: "back.in(2)",
      }),
  },
  "animation-14": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotation: -360 },
        {
          opacity: 1,
          rotation: 0,
          stagger: 0.05,
          duration: 1,
          ease: "power2.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotation: 360,
        stagger: 0.05,
        duration: 1,
        ease: "power2.in",
      }),
  },
  "animation-15": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, x: 200 },
        { opacity: 1, x: 0, stagger: 0.05, duration: 1, ease: "power3.out" }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        x: -200,
        stagger: 0.05,
        duration: 1,
        ease: "power3.in",
      }),
  },
  "animation-16": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: -150 },
        {
          opacity: 1,
          y: 0,
          stagger: 0.05,
          duration: 1.3,
          ease: "back.out(1)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: 150,
        stagger: 0.05,
        duration: 1.3,
        ease: "back.in(1)",
      }),
  },
  "animation-17": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, scale: 0 },
        {
          opacity: 1,
          scale: 1,
          stagger: 0.05,
          duration: 1.2,
          ease: "elastic.out(1, 0.4)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        scale: 0,
        stagger: 0.05,
        duration: 1.2,
        ease: "elastic.in(1, 0.4)",
      }),
  },
  "animation-18": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, scale: 0.6 },
        {
          opacity: 1,
          scale: 1,
          stagger: 0.05,
          duration: 1.4,
          ease: "back.out(1.5)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        scale: 0.6,
        stagger: 0.05,
        duration: 1.4,
        ease: "back.in(1.5)",
      }),
  },
  "animation-19": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotationY: -180 },
        {
          opacity: 1,
          rotationY: 0,
          stagger: 0.05,
          duration: 1.2,
          ease: "power2.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotationY: 180,
        stagger: 0.05,
        duration: 1.2,
        ease: "power2.in",
      }),
  },
  "animation-20": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: 200 },
        {
          opacity: 1,
          y: 0,
          stagger: 0.05,
          duration: 1.5,
          ease: "elastic.out(1, 0.3)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: -200,
        stagger: 0.05,
        duration: 1.5,
        ease: "elastic.in(1, 0.3)",
      }),
  },
  "animation-21": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotationZ: -360 },
        {
          opacity: 1,
          rotationZ: 0,
          stagger: 0.05,
          duration: 1.6,
          ease: "bounce.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotationZ: 360,
        stagger: 0.05,
        duration: 1.6,
        ease: "bounce.in",
      }),
  },
  "animation-22": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, x: -150 },
        {
          opacity: 1,
          x: 0,
          stagger: 0.05,
          duration: 1.5,
          ease: "elastic.out(1, 0.3)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        x: 150,
        stagger: 0.05,
        duration: 1.5,
        ease: "elastic.in(1, 0.3)",
      }),
  },
  "animation-23": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, scale: 0.4 },
        {
          opacity: 1,
          scale: 1,
          stagger: 0.05,
          duration: 1.2,
          ease: "bounce.out",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        scale: 0.4,
        stagger: 0.05,
        duration: 1.2,
        ease: "bounce.in",
      }),
  },
  "animation-24": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: 100 },
        {
          opacity: 1,
          y: 0,
          stagger: 0.05,
          duration: 1.2,
          ease: "back.out(1)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: -100,
        stagger: 0.05,
        duration: 1.2,
        ease: "back.in(1)",
      }),
  },
  "animation-25": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, scale: 0.7 },
        {
          opacity: 1,
          scale: 1,
          stagger: 0.05,
          duration: 1.5,
          ease: "elastic.out(1, 0.4)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        scale: 0.7,
        stagger: 0.05,
        duration: 1.5,
        ease: "elastic.in(1, 0.4)",
      }),
  },
  "animation-26": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotationY: 90 },
        {
          opacity: 1,
          rotationY: 0,
          stagger: 0.05,
          duration: 1.2,
          ease: "back.out(1)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotationY: -90,
        stagger: 0.05,
        duration: 1.2,
        ease: "back.in(1)",
      }),
  },
  "animation-27": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: -200 },
        { opacity: 1, y: 0, stagger: 0.05, duration: 1.3, ease: "power3.out" }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: 200,
        stagger: 0.05,
        duration: 1.3,
        ease: "power3.in",
      }),
  },
  "animation-28": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, rotationX: -90 },
        {
          opacity: 1,
          rotationX: 0,
          stagger: 0.05,
          duration: 1.4,
          ease: "elastic.out(1, 0.4)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        rotationX: 90,
        stagger: 0.05,
        duration: 1.4,
        ease: "elastic.in(1, 0.4)",
      }),
  },
  "animation-29": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, x: 200 },
        {
          opacity: 1,
          x: 0,
          stagger: 0.05,
          duration: 1.5,
          ease: "elastic.out(1, 0.3)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        x: -200,
        stagger: 0.05,
        duration: 1.5,
        ease: "elastic.in(1, 0.3)",
      }),
  },
  "animation-30": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, scale: 0.5 },
        {
          opacity: 1,
          scale: 1,
          stagger: 0.05,
          duration: 1.6,
          ease: "back.out(2)",
        }
      ),
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        scale: 0.5,
        stagger: 0.05,
        duration: 1.6,
        ease: "back.in(2)",
      }),
  },

  "animation-31": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        {
          opacity: 0,
          scale: 5,
          y: 0,
          transformOrigin: "50% 50%",
          willChange: "transform, opacity",
        },
        {
          opacity: 1,
          scale: 1,
          y: 0,
          stagger: {
            amount: 1,
            from: "center", // Stagger from center for "in"
          },
          ease: "power4.out",
          duration: 0.4,
        }
      ),
    out: (chars) =>
      gsap.fromTo(
        chars,
        {
          opacity: 1,
          scale: 1,
          y: 0,
          transformOrigin: "50% 50%",
          willChange: "transform, opacity",
        },
        {
          opacity: 0,
          scale: 5,
          y: 0,
          stagger: {
            amount: 1,
            from: "edges", // Stagger from left and right for "out"
          },
          ease: "power4.in",
          duration: 0.2,
        }
      ),
  },
  "animation-32": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: 100 },  // Start from below (y = 100)
        {
          opacity: 1,
          y: 0,  // Move to its original position (y = 0)
          duration: 1,
          ease: "power2.out",
        }
      ),
    
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: -100,  // Move upwards (y = -100) for out animation
        duration: 1,
        ease: "power2.in",
      }),
  },
  "animation-33": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, y: 100 },  // Start from below (y = 100)
        {
          opacity: 1,
          y: 0,  // Move to its original position (y = 0)
          duration: 0.3,
          ease: "power2.out",
        }
      ),
    
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        y: 100,  // Move upwards (y = -100) for out animation
        duration: 0.3,
        ease: "power2.in",
      }),
  },
  "animation-34": {
    in: (chars) =>
      gsap.fromTo(
        chars,
        { opacity: 0, x: 100 },  // Start from below (y = 100)
        {
          opacity: 1,
          y: 0,  // Move to its original position (y = 0)
          duration: 1,
          ease: "power2.out",
        }
      ),
    
    out: (chars) =>
      gsap.to(chars, {
        opacity: 0,
        x: -100,  // Move upwards (y = -100) for out animation
        duration: 1,
        ease: "power2.in",
      }),
  },
  "animation-35": {
in: (chars) =>
  gsap.fromTo(
    chars,
    { opacity: 0, x: -100 },  // Start from the left (x = -100)
    {
      opacity: 1,
      x: 0,  // Move to its original position (x = 0)
      duration: 1,
      ease: "power2.out",
    }
  ),

out: (chars) =>
  gsap.to(chars, {
    opacity: 0,
    x: 100,  // Move to the right (x = 100) for out animation
    duration: 1,
    ease: "power2.in",
  }),
},
"animation-36": {
in: (chars) =>
  gsap.fromTo(
    chars,
    { opacity: 0, x: -100 },  // Start from the left (x = -100)
    {
      opacity: 1,
      x: 0,  // Move to its original position (x = 0)
      duration: 1,
      ease: "power2.out",
    }
  ),

out: (chars) =>
  gsap.to(chars, {
    opacity: 0,
    x: -100,  // Move back to the left (x = -100) for out animation
    duration: 1,
    ease: "power2.in",
  }),
},
"animation-37": {
  in: (chars) =>
    gsap.fromTo(
      chars,
      { opacity: 0, x: -100 },  // Start from the left (x = -100)
      {
        opacity: 1,
        x: 0,  // Move to its original position (x = 0)
        duration: 1,
        ease: "power2.out",
        stagger: 0.1,  // Add stagger effect with 0.1 seconds delay between each character
      }
    ),

  out: (chars) =>
    gsap.to(chars, {
      opacity: 0,
      x: -100,  // Move back to the left (x = -100) for out animation
      duration: 1,
      ease: "power2.in",
      stagger: 0.1,  // Add stagger effect with 0.1 seconds delay between each character
    }),
},

};

