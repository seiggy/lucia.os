async (page) => {
  const root = 'E:\\github\\lucia-os\\.impeccable\\mocks';
  const url = 'file:///E:/github/lucia-os/.impeccable/mocks/dashboard-layouts.html';
  const results = await Promise.all(['a', 'b', 'c'].map(async (layout) => {
    const tab = await page.context().newPage();
    try {
      await tab.setViewportSize({ width: 1120, height: 1000 });
      await tab.goto(`${url}?layout=${layout}`);
      await tab.locator('.device').first().waitFor();
      if (await tab.locator('.device').count() !== 4) throw new Error(`Missing devices in ${layout}`);
      await tab.screenshot({ path: `${root}\\layout-${layout}-desktop.png`, fullPage: true });
      await tab.setViewportSize({ width: 390, height: 844 });
      if (await tab.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error(`Mobile overflow in ${layout}`);
      await tab.screenshot({ path: `${root}\\layout-${layout}-phone.png` });
      await tab.getByRole('link', { name: /Review fix|Home server.*Needs your okay/ }).first().click();
      await tab.getByRole('heading', { name: "Let's get backups running." }).waitFor();
      if (await tab.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error(`Incident overflow in ${layout}`);
      return { layout, devices: 4, mobileOverflow: false, incidentLink: true };
    } finally {
      await tab.close();
    }
  }));
  await page.setViewportSize({ width: 1120, height: 850 });
  await page.goto(`${url}?layout=a&screen=incident`);
  await page.screenshot({ path: `${root}\\incident-desktop.png`, fullPage: true });
  return results;
}
