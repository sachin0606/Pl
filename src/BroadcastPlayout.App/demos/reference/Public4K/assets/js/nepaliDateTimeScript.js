// Global Variables
let currentDate = new Date().toDateString(); // Track current system date
const timeEl = document.getElementById("time");
const dateEl = document.getElementById("date");
const dayEl = document.getElementById("day");

// Mapping digits to symbols
const digitMap = {
  '0': ')',
  '1': '!',
  '2': '@',
  '3': '#',
  '4': '$',
  '5': '%',
  '6': '^',
  '7': '&',
  '8': '*',
  '9': '('
};

// Function to map digits to symbols
function mapDigitsToSymbols(text) {
  return text.replace(/\d/g, digit => digitMap[digit]);
}

// Function to update Nepali date and time
function updateNepaliDateTime() {
  const today = new Date();
  const dayOfWeek = today.getDay();

  // Nepali date conversions
  const adday = NepaliFunctions.GetAdDay(dayOfWeek);
  const nepaliDay = NepaliFunctions.GetBsDayUnicode(dayOfWeek);
  const currentMonth = NepaliFunctions.GetCurrentBsMonth();
  const bsMonthUnicode = NepaliFunctions.GetBsMonthInUnicode(currentMonth - 1);
  const bsCurrentDay = NepaliFunctions.GetCurrentBsDay();

  // Preeti font conversion
  const convertedDay = `<span class="nepali-font-Arab">${convert_to_Preeti(nepaliDay)}</span>`;
  const convertedMonth = `<span class="nepali-font-Arab">${convert_to_Preeti(bsMonthUnicode)}</span>`;
  const bsCurrentDate = `<span class="nepali-font-Arab">${bsCurrentDay}</span>`;

  // Update DOM elements
  dayEl.innerHTML = convertedDay;
  dateEl.innerHTML = convertedMonth + '&nbsp;' + mapDigitsToSymbols(bsCurrentDate);}

// Function to update time
function updateTime() {
  const now = new Date();

  // Get time in 24-hour format without seconds
  const timeString = now.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

  // Transform time components
  const [hours, minutes] = timeString.split(':');
  const transformedHours = mapDigitsToSymbols(hours);
  const transformedMinutes = mapDigitsToSymbols(minutes);

  // Update time element
  timeEl.innerHTML = `<span class="nepali-font-Arab">${transformedHours}M</span> <span class="nepali-font-Arab">${transformedMinutes}</span>`;
}

// Function to detect date change
function checkDateChange() {
  const newDate = new Date().toDateString();
  if (newDate !== currentDate) {
    currentDate = newDate;
    updateNepaliDateTime(); // Update Nepali date when global date changes
  }
}


// Initialize and set intervals
updateNepaliDateTime(); // Initial update for Nepali date and time
updateTime(); // Initial time update
setInterval(updateTime, 1000); // Update time every second
setInterval(checkDateChange, 60000); // Check for global date changes every minute
// startSlider(); // Start the slider
